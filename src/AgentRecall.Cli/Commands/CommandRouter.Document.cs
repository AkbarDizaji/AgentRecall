using AgentRecall.Core.Activity;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.DocOpportunity;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentRecall.Cli;

// The `document` command group: on-demand document-opportunity coaching and generation. The
// host-supplied judge runs at end-of-turn (see finalize-turn) and only ever surfaces a short
// pointer; a file is written only here, on demand, after the user has explicitly agreed in
// chat. No LLM, embeddings, or network are involved — AgentRecall supplies no document content
// itself, only the type/title/confidence signal and the write mechanics.
public static partial class CommandRouter
{
    private static async Task<int> DocumentAsync(
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

        switch (sub)
        {
            case "write":
                return await DocumentWriteAsync(scope.ServiceProvider, options, output, logger, cancellationToken)
                    .ConfigureAwait(false);

            case "status":
                return await DocumentStatusAsync(scope.ServiceProvider, options.ContainsKey("json"), output, cancellationToken)
                    .ConfigureAwait(false);

            default:
                DocumentUsage(output);
                return 1;
        }
    }

    private static async Task<int> DocumentStatusAsync(
        IServiceProvider services,
        bool json,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var options = services.GetRequiredService<AgentRecallOptions>();
        var docOpportunity = services.GetRequiredService<IDocOpportunityService>();
        var last = await docOpportunity.GetLastAsync(cancellationToken).ConfigureAwait(false);
        var mode = options.ResolvedDocOpportunityMode;

        if (json)
        {
            WriteJson(output, new
            {
                mode = mode.ToString(),
                last_candidate = last is null ? null : DocOpportunityJson(last),
            });
            return 0;
        }

        output.WriteLine("AgentRecall Document Opportunity");
        output.WriteLine();
        output.WriteLine($"Mode:            {mode}");
        output.WriteLine(DocOpportunityRenderer.RenderStatus(last));
        if (last is { Status: DocOpportunityStatus.Open })
        {
            output.WriteLine();
            output.WriteLine("Ask the user, then run `agentrecall document write --type <type> --title \"<title>\"`" +
                              " (drafted content piped on stdin) to generate it.");
        }

        return 0;
    }

    /// <summary>
    /// <c>document write --type &lt;T&gt; --title "..."  [--turn-id id] [--root path] [--force] [--json]</c>:
    /// writes the Markdown body piped on stdin to <c>{root}/{typeFolder}/{date}-{slug}.md</c>.
    /// Never overwrites by default — a naming collision auto-suffixes (<c>-2</c>, <c>-3</c>, ...);
    /// <c>--force</c> opts into overwriting in place. <c>--turn-id</c> is best-effort: when it
    /// matches an offered candidate, that candidate is marked written; the write still succeeds
    /// without one.
    /// </summary>
    private static async Task<int> DocumentWriteAsync(
        IServiceProvider services,
        Dictionary<string, string> options,
        TextWriter output,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("type", out var typeText) ||
            !Enum.TryParse<DocumentType>(typeText, ignoreCase: true, out var type) ||
            !Enum.IsDefined(type))
        {
            output.WriteLine("Usage: agentrecall document write --type <Incident|Rfc|Proposal|Adr|Postmortem|Runbook> " +
                              "--title \"<title>\" [--turn-id <id>] [--root <path>] [--force] [--json]");
            output.WriteLine("The document body is read from stdin.");
            return 1;
        }

        if (!options.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title))
        {
            output.WriteLine("Missing --title.");
            return 1;
        }

        var json = options.ContainsKey("json");
        var force = options.ContainsKey("force");
        var root = options.TryGetValue("root", out var rootOption) && !string.IsNullOrWhiteSpace(rootOption)
            ? rootOption
            : services.GetRequiredService<AgentRecallOptions>().DocOpportunityRoot;

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            output.WriteLine($"Invalid --root path '{root}': {ex.Message}");
            return 1;
        }

        var content = await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var folder = Path.Combine(fullRoot, DocumentTypeNames.FolderName(type));
        var datePrefix = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var slug = DocSlug.Slugify(title);

        string path;
        try
        {
            Directory.CreateDirectory(folder);
            path = ResolveWritePath(folder, datePrefix, slug, force);
            await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            logger.LogError(ex, "Failed to write document file.");
            output.WriteLine($"Failed to write document: {ex.Message}");
            return 1;
        }

        if (options.TryGetValue("turn-id", out var turnId) && !string.IsNullOrWhiteSpace(turnId))
        {
            var docOpportunity = services.GetRequiredService<IDocOpportunityService>();
            var candidate = await docOpportunity.GetForTurnAsync(turnId, cancellationToken).ConfigureAwait(false);
            if (candidate is not null)
            {
                await docOpportunity.MarkWrittenAsync(candidate.Id, path, cancellationToken).ConfigureAwait(false);
            }
        }

        if (json)
        {
            WriteJson(output, new { path, document_type = type.ToString(), title });
        }
        else
        {
            output.WriteLine($"Wrote {DocumentTypeNames.DisplayName(type)} document to {path}.");
        }

        return 0;
    }

    /// <summary>
    /// Resolves the file to write: the plain <c>{date}-{slug}.md</c> path when <paramref name="force"/>
    /// is set or nothing occupies it, otherwise the first free <c>-2</c>, <c>-3</c>, ... suffix —
    /// never overwriting existing content.
    /// </summary>
    private static string ResolveWritePath(string folder, string datePrefix, string slug, bool force)
    {
        var baseName = $"{datePrefix}-{slug}";
        var plain = Path.Combine(folder, baseName + ".md");
        if (force || !File.Exists(plain))
        {
            return plain;
        }

        for (var i = 2; i < 10_000; i++)
        {
            var suffixed = Path.Combine(folder, $"{baseName}-{i}.md");
            if (!File.Exists(suffixed))
            {
                return suffixed;
            }
        }

        throw new IOException($"Could not find a free filename for '{baseName}' after 10000 attempts.");
    }

    /// <summary>
    /// Runs the document-opportunity judge for a finalized turn, persisting a candidate and
    /// recording a human-visible notice when one is offered. Returns the candidate (or null when
    /// the mode is off or nothing was offered). Best-effort: any failure is swallowed so it never
    /// blocks turn finalization.
    /// </summary>
    private static async Task<DocOpportunityCandidate?> AnalyzeDocOpportunityAsync(
        IServiceProvider services,
        TurnFinalizationInput input,
        TurnFinalizationResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var docOpportunity = services.GetRequiredService<IDocOpportunityService>();
            var candidate = await docOpportunity.AnalyzeTurnAsync(new DocOpportunityTurnRequest
            {
                Prompt = input.Prompt,
                Response = input.AssistantResponse,
                TurnId = result.TurnId,
                Source = input.Source ?? "cli",
                ScopeLevel = input.ScopeLevel,
                ScopeValue = input.ScopeValue,
                SuppliedVerdict = input.SuppliedDocOpportunity,
            }, cancellationToken).ConfigureAwait(false);

            if (candidate is null)
            {
                return null;
            }

            var notice = ActivityNoticeFactory.ForDocOpportunity(candidate, input.Source ?? "cli");
            if (notice is not null)
            {
                await services.GetRequiredService<IActivityRecorder>()
                    .RecordAsync(notice with { TurnId = result.TurnId }, cancellationToken).ConfigureAwait(false);
            }

            return candidate;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Document opportunity is advisory; never let it break finalization.
            Console.Error.WriteLine($"[agentrecall] document-opportunity detection skipped: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Prints the automatic document-opportunity summary on the human-facing CLI path (never
    /// the model-visible hook path, which gets only the short turn-summary pointer).
    /// </summary>
    private static void PrintDocOpportunity(TextWriter output, DocOpportunityCandidate? candidate)
    {
        if (candidate is null)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine(DocOpportunityRenderer.RenderCompact(candidate));
    }

    /// <summary>
    /// Deterministic, snake_case JSON for a document-opportunity candidate. Contains no
    /// Markdown so it can be consumed programmatically; null yields the "nothing offered" shape.
    /// </summary>
    private static object DocOpportunityJson(DocOpportunityCandidate? candidate)
    {
        if (candidate is null)
        {
            return new { turn_id = (string?)null, document_type = (string?)null };
        }

        return new
        {
            turn_id = string.IsNullOrEmpty(candidate.TurnId) ? null : candidate.TurnId,
            document_type = candidate.DocumentType.ToString(),
            suggested_title = candidate.SuggestedTitle,
            confidence = candidate.Confidence,
            reason = candidate.Reason,
            key_points = DocOpportunityMapping.KeyPoints(candidate).ToArray(),
            status = candidate.Status.ToString(),
            written_path = string.IsNullOrEmpty(candidate.WrittenPath) ? null : candidate.WrittenPath,
        };
    }

    private static void DocumentUsage(TextWriter output)
    {
        output.WriteLine("Usage:");
        output.WriteLine("  agentrecall document write --type <Incident|Rfc|Proposal|Adr|Postmortem|Runbook>");
        output.WriteLine("      --title \"<title>\" [--turn-id <id>] [--root <path>] [--force] [--json]");
        output.WriteLine("      (the document body is read from stdin)");
        output.WriteLine("  agentrecall document status [--json]");
        output.WriteLine();
        output.WriteLine("The document-opportunity judge runs at end-of-turn only when");
        output.WriteLine("AgentRecall.DocOpportunityMode is not Off. Offering a document never writes a");
        output.WriteLine("file by itself — only `document write` does, on demand.");
    }
}
