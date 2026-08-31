using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentRecall.Cli;

// The `cleanup` command group: safe, reviewable removal of already-stored noise. Today it
// covers noisy Pending rules the Stop hook created before capture was hardened. It archives
// (never hard-deletes), defaults to a dry run, and never touches Active/Promoted or
// user-modified rules or rules that read as clean lessons.
public static partial class CommandRouter
{
    /// <summary>Tag the Stop-hook finalizer stamps on every rule it creates.</summary>
    private const string FinalizerTag = "turn-finalizer";

    private static async Task<int> CleanupAsync(
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
            case "pending-noise":
                return await CleanupPendingNoiseAsync(scope, options, output, logger, cancellationToken).ConfigureAwait(false);

            default:
                CleanupUsage(output);
                return 1;
        }
    }

    private static async Task<int> CleanupPendingNoiseAsync(
        AsyncServiceScope scope,
        Dictionary<string, string> options,
        TextWriter output,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var json = options.ContainsKey("json");
        var apply = options.ContainsKey("apply");
        var tag = options.TryGetValue("tag", out var t) && !string.IsNullOrWhiteSpace(t) ? t.Trim() : FinalizerTag;
        var status = ResolveCleanupStatus(options);

        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var all = await rules.ListAsync(cancellationToken).ConfigureAwait(false);

        // Second-pass duplicate detection: the same noisy body stored across turns.
        var seenBodies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matched = new List<(RecallRule Rule, CaptureSkipReason Reason)>();

        foreach (var rule in all.OrderBy(r => r.Id))
        {
            if (rule.Status != status || !HasTag(rule, tag))
            {
                continue;
            }

            // Never archive rules the user made their own (edited or versioned), and never
            // Active/Promoted rules (the status filter already excludes them by default).
            if (IsUserOwned(rule))
            {
                continue;
            }

            var reason = ClassifyNoise(rule);
            if (reason == CaptureSkipReason.None)
            {
                continue; // a clean lesson — leave it alone
            }

            var bodyKey = (rule.RuleText ?? string.Empty).Trim().ToLowerInvariant();
            if (bodyKey.Length > 0 && !seenBodies.Add(bodyKey))
            {
                reason = CaptureSkipReason.DuplicateNoise;
            }

            matched.Add((rule, reason));
        }

        var reasonCounts = matched
            .GroupBy(m => m.Reason)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        var archived = 0;
        if (apply && matched.Count > 0)
        {
            var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();
            foreach (var (rule, _) in matched)
            {
                try
                {
                    await lifecycle.ArchiveAsync(rule.Id, cancellationToken).ConfigureAwait(false);
                    archived++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to archive noisy pending rule #{RuleId}.", rule.Id);
                }
            }
        }

        if (json)
        {
            WriteJson(output, new
            {
                matched = matched.Count,
                archived,
                dryRun = !apply,
                tag,
                status = status.ToString(),
                reasons = reasonCounts,
            });
            return 0;
        }

        RenderPendingNoise(output, matched.Count, archived, apply, reasonCounts);
        return 0;
    }

    private static void RenderPendingNoise(
        TextWriter output, int matched, int archived, bool apply, IReadOnlyDictionary<string, int> reasons)
    {
        if (matched == 0)
        {
            output.WriteLine("🧠 **AgentRecall:** found 0 noisy pending rules. Nothing to clean up.");
            return;
        }

        if (apply)
        {
            output.WriteLine($"🧠 **AgentRecall:** archived {archived} noisy pending {Plural(archived, "rule")}.");
        }
        else
        {
            output.WriteLine($"🧠 **AgentRecall:** found {matched} noisy pending {Plural(matched, "rule")}.");
        }

        foreach (var (reason, count) in reasons)
        {
            output.WriteLine($"- {ReasonLabel(reason)}: {count}");
        }

        if (!apply)
        {
            output.WriteLine("Run `agentrecall cleanup pending-noise --apply` to archive them.");
        }
    }

    /// <summary>
    /// Classifies a stored rule as noise using the same deterministic gate the Stop hook
    /// uses at capture time: an assistant-prose / vague / do-not-save body, or a malformed
    /// conversation-fragment trigger. Returns <see cref="CaptureSkipReason.None"/> for a
    /// clean rule that has a real body and a real condition.
    /// </summary>
    private static CaptureSkipReason ClassifyNoise(RecallRule rule)
    {
        var body = StopHookCandidateGate.ScreenText(rule.RuleText);
        if (!body.IsAcceptable)
        {
            return body.Reason;
        }

        return StopHookCandidateGate.IsMalformedTrigger(rule.Trigger)
            ? CaptureSkipReason.MalformedTrigger
            : CaptureSkipReason.None;
    }

    // A rule the user edited or promoted is theirs to keep — never auto-archived.
    private static bool IsUserOwned(RecallRule rule) =>
        rule.Version > 1 || rule.Status is RuleStatus.Promoted or RuleStatus.Active;

    private static bool HasTag(RecallRule rule, string tag) =>
        !string.IsNullOrEmpty(rule.Tags) &&
        rule.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

    // Default to Pending; honour an explicit --status but never widen to Active/Promoted,
    // which IsUserOwned also guards, so this command can never archive a live rule.
    private static RuleStatus ResolveCleanupStatus(Dictionary<string, string> options)
    {
        if (options.TryGetValue("status", out var raw) &&
            Enum.TryParse<RuleStatus>(raw, ignoreCase: true, out var parsed) &&
            parsed is RuleStatus.Pending or RuleStatus.Draft)
        {
            return parsed;
        }

        return RuleStatus.Pending;
    }

    private static string Plural(int count, string singular) => count == 1 ? singular : singular + "s";

    private static string ReasonLabel(string reason) => reason switch
    {
        nameof(CaptureSkipReason.AssistantProse) => "Assistant prose",
        nameof(CaptureSkipReason.MalformedTrigger) => "Malformed trigger",
        nameof(CaptureSkipReason.DuplicateNoise) => "Duplicate noise",
        nameof(CaptureSkipReason.TooVague) => "Too vague",
        nameof(CaptureSkipReason.MissingAction) => "Missing action",
        nameof(CaptureSkipReason.ExplicitDoNotSave) => "Do-not-save",
        nameof(CaptureSkipReason.CodeFact) => "Code fact",
        nameof(CaptureSkipReason.SourceDocument) => "Source-document instruction",
        nameof(CaptureSkipReason.ToolOrSkillInstruction) => "Tool/skill instruction",
        nameof(CaptureSkipReason.CommandOutput) => "Command output",
        nameof(CaptureSkipReason.LogOutput) => "Log output",
        _ => reason,
    };

    private static void CleanupUsage(TextWriter output)
    {
        output.WriteLine("Usage:");
        output.WriteLine("  agentrecall cleanup pending-noise [--apply] [--json] [--tag <tag>] [--status <status>]");
        output.WriteLine();
        output.WriteLine("Finds noisy Pending rules AgentRecall's end-of-turn capture created (assistant prose, malformed");
        output.WriteLine("triggers, duplicates) and archives them. Dry run by default; pass --apply to archive.");
    }
}
