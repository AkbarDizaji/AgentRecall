using AgentRecall.Core.Activity;
using AgentRecall.Core.CareerImpact;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentRecall.Cli;

// The `career` command group: on-demand career-impact coaching. The automatic detector runs
// at end-of-turn (see finalize-turn); these commands read back what it found and generate the
// promotion-ready journal only when asked. No LLM, embeddings, or network are involved.
public static partial class CommandRouter
{
    private static async Task<int> CareerAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var sub = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : string.Empty;
        var options = ParseOptions(args);

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var career = scope.ServiceProvider.GetRequiredService<ICareerImpactService>();

        switch (sub)
        {
            case "impact":
                return await CareerImpactAsync(career, options, output, cancellationToken).ConfigureAwait(false);

            case "journal":
                return await CareerJournalAsync(career, options, output, logger, cancellationToken).ConfigureAwait(false);

            case "status":
                return await CareerStatusAsync(scope.ServiceProvider, career, options.ContainsKey("json"), output, cancellationToken).ConfigureAwait(false);

            default:
                CareerUsage(output);
                return 1;
        }
    }

    private static async Task<int> CareerImpactAsync(
        ICareerImpactService career,
        Dictionary<string, string> options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var candidate = await career.GetLastAsync(cancellationToken).ConfigureAwait(false);

        if (options.ContainsKey("json"))
        {
            WriteJson(output, CareerImpactJson(candidate));
            return 0;
        }

        if (candidate is null)
        {
            output.WriteLine(CareerImpactRenderer.NoImpactMessage);
            return 0;
        }

        var analysis = CareerImpactMapping.ToAnalysis(candidate);
        output.WriteLine(options.ContainsKey("detailed")
            ? CareerImpactRenderer.RenderDetailed(analysis)
            : CareerImpactRenderer.RenderCompact(analysis));
        return 0;
    }

    private static async Task<int> CareerJournalAsync(
        ICareerImpactService career,
        Dictionary<string, string> options,
        TextWriter output,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var candidate = await career.GetLastAsync(cancellationToken).ConfigureAwait(false);

        if (candidate is null)
        {
            if (options.ContainsKey("json"))
            {
                WriteJson(output, CareerImpactJson(null));
                return 0;
            }

            output.WriteLine(CareerImpactRenderer.NoImpactMessage);
            return 0;
        }

        var analysis = CareerImpactMapping.ToAnalysis(candidate);

        if (options.ContainsKey("json"))
        {
            WriteJson(output, CareerImpactJson(candidate));
            return 0;
        }

        var entry = CareerImpactRenderer.RenderJournal(analysis, candidate.CreatedAt);

        if (options.TryGetValue("file", out var path) && !string.IsNullOrWhiteSpace(path))
        {
            return await WriteJournalFileAsync(path, entry, output, logger, cancellationToken).ConfigureAwait(false);
        }

        output.WriteLine(entry);
        return 0;
    }

    /// <summary>
    /// Appends the journal entry to a Markdown file (creating it when missing), never
    /// overwriting existing content, and reports where it wrote. Validates the path.
    /// </summary>
    private static async Task<int> WriteJournalFileAsync(
        string path,
        string entry,
        TextWriter output,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            output.WriteLine($"Invalid --file path '{path}': {ex.Message}");
            return 1;
        }

        try
        {
            var existed = File.Exists(fullPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Append to preserve prior entries; separate them with a blank line.
            var block = existed ? "\n" + entry.TrimEnd() + "\n" : entry.TrimEnd() + "\n";
            await File.AppendAllTextAsync(fullPath, block, cancellationToken).ConfigureAwait(false);

            output.WriteLine(existed
                ? $"Appended career journal entry to {fullPath}."
                : $"Wrote career journal entry to {fullPath}.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Failed to write career journal file.");
            output.WriteLine($"Failed to write '{fullPath}': {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> CareerStatusAsync(
        IServiceProvider services,
        ICareerImpactService career,
        bool json,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var options = services.GetRequiredService<AgentRecallOptions>();
        var installed = await career.IsPackInstalledAsync(cancellationToken).ConfigureAwait(false);
        var last = await career.GetLastAsync(cancellationToken).ConfigureAwait(false);
        var mode = options.ResolvedCareerImpactMode;
        var level = options.ResolvedCareerImpactSummaryLevel;

        if (json)
        {
            WriteJson(output, new
            {
                pack_installed = installed,
                mode = mode.ToString(),
                summary_level = level.ToString(),
                last_candidate = last is null ? null : new
                {
                    turn_id = string.IsNullOrEmpty(last.TurnId) ? null : last.TurnId,
                    is_significant = last.IsSignificant,
                    confidence = last.Confidence,
                    promotion_worthiness = last.PromotionWorthiness,
                    status = last.Status.ToString(),
                },
            });
            return 0;
        }

        output.WriteLine("AgentRecall Career Impact");
        output.WriteLine();
        output.WriteLine($"Pack installed:  {(installed ? "yes" : "no — install with `agentrecall seed install career-impact`")}");
        output.WriteLine($"Mode:            {mode}");
        output.WriteLine($"Summary level:   {level}");
        if (last is null)
        {
            output.WriteLine("Last candidate:  (none detected yet)");
        }
        else
        {
            output.WriteLine($"Last candidate:  {(last.IsSignificant ? "significant" : "low-confidence")} " +
                $"(confidence {last.Confidence:0.00}, worthiness {last.PromotionWorthiness}/10, {last.Status})");
            output.WriteLine("Run `agentrecall career impact --last` or `agentrecall career journal --last` for detail.");
        }

        return 0;
    }

    /// <summary>
    /// Deterministic, snake_case JSON for a career-impact candidate. Contains no Markdown so
    /// it can be consumed programmatically; null yields the "not significant" shape.
    /// </summary>
    private static object CareerImpactJson(CareerImpactCandidate? candidate)
    {
        if (candidate is null)
        {
            return new { turn_id = (string?)null, is_significant = false };
        }

        var analysis = CareerImpactMapping.ToAnalysis(candidate);
        return new
        {
            turn_id = string.IsNullOrEmpty(candidate.TurnId) ? null : candidate.TurnId,
            is_significant = analysis.IsSignificant,
            confidence = analysis.Confidence,
            promotion_worthiness = analysis.PromotionWorthiness,
            categories = analysis.Categories.Select(c => c.ToString()).ToArray(),
            why_this_matters = analysis.WhyThisMatters,
            technical_impact = analysis.TechnicalImpact,
            business_impact = analysis.BusinessImpact,
            long_term_impact = analysis.LongTermImpact,
            evidence_to_collect = analysis.SuggestedEvidence.ToArray(),
            metrics = analysis.SuggestedMetrics.ToArray(),
            stakeholders = analysis.Stakeholders.ToArray(),
            adr = new
            {
                recommended = analysis.Adr.Recommended,
                suggested_title = analysis.Adr.SuggestedTitle,
                context = analysis.Adr.Context,
                decision = analysis.Adr.Decision,
                alternatives = analysis.Adr.Alternatives.ToArray(),
                consequences = analysis.Adr.Consequences.ToArray(),
            },
            promotion_note = analysis.PromotionNote,
            next_actions = analysis.NextActions.ToArray(),
        };
    }

    /// <summary>
    /// Runs the end-of-turn career-impact detector for a finalized turn, persisting a
    /// candidate and recording a human-visible notice when the mode surfaces one. Returns the
    /// candidate (or null when the pack is off, the mode is Silent, or nothing was surfaced).
    /// Best-effort: any failure is swallowed so it never blocks turn finalization.
    /// </summary>
    private static async Task<CareerImpactCandidate?> AnalyzeCareerImpactAsync(
        IServiceProvider services,
        TurnFinalizationInput input,
        TurnFinalizationResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var career = services.GetRequiredService<ICareerImpactService>();
            var candidate = await career.AnalyzeTurnAsync(new CareerImpactTurnRequest
            {
                Prompt = input.Prompt,
                Response = input.AssistantResponse,
                CapturedRuleTexts = result.Captured.Select(c => c.Text).ToList(),
                TurnId = result.TurnId,
                Source = input.Source ?? "cli",
            }, cancellationToken).ConfigureAwait(false);

            if (candidate is null)
            {
                return null;
            }

            var notice = ActivityNoticeFactory.ForCareerImpact(candidate, input.Source ?? "cli");
            if (notice is not null)
            {
                await services.GetRequiredService<IActivityRecorder>()
                    .RecordAsync(notice with { TurnId = result.TurnId }, cancellationToken).ConfigureAwait(false);
            }

            return candidate;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Career impact is advisory; never let it break finalization.
            Console.Error.WriteLine($"[agentrecall] career-impact detection skipped: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Prints the automatic career-impact summary on the human-facing CLI path (never the
    /// model-visible hook path). Compact or detailed per <c>CareerImpactSummaryLevel</c>.
    /// </summary>
    private static void PrintCareerImpact(TextWriter output, CareerImpactCandidate? candidate, AgentRecallOptions options)
    {
        if (candidate is null)
        {
            return;
        }

        var analysis = CareerImpactMapping.ToAnalysis(candidate);
        var text = options.ResolvedCareerImpactSummaryLevel == CareerImpactSummaryLevel.Detailed
            ? CareerImpactRenderer.RenderDetailed(analysis)
            : CareerImpactRenderer.RenderCompact(analysis);

        output.WriteLine();
        output.WriteLine(text);
    }

    private static void CareerUsage(TextWriter output)
    {
        output.WriteLine("Usage:");
        output.WriteLine("  agentrecall career impact --last [--json] [--detailed]");
        output.WriteLine("  agentrecall career journal --last [--json] [--file <path>]");
        output.WriteLine("  agentrecall career status [--json]");
        output.WriteLine();
        output.WriteLine("The career-impact detector runs at end-of-turn only when the `career-impact`");
        output.WriteLine("seed pack is installed and AgentRecall.CareerImpactMode is not Silent.");
    }
}
