using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli;

/// <summary>What Interactive Memory did with a candidate.</summary>
public enum InteractiveMemoryOutcome
{
    /// <summary>A strong lesson was captured automatically; no question asked.</summary>
    AutoCaptured,

    /// <summary>A duplicate reinforced an existing rule; nothing new stored.</summary>
    ReusedDuplicate,

    /// <summary>A low-value candidate was skipped; no question asked.</summary>
    Skipped,

    /// <summary>An ambiguous candidate was parked as Pending without a prompt (hook/non-interactive/Silent).</summary>
    PendingSuggested,

    /// <summary>The user chose to remember the suggestion; it is now Active.</summary>
    Remembered,

    /// <summary>The user chose to ignore the suggestion; it is now Archived.</summary>
    Ignored,
}

/// <summary>
/// Surfaces the deterministic capture decision as a lightweight interaction. It does not
/// make the capture decision (the <see cref="ICaptureDecisionPolicy"/> already did) and
/// it does not change worthiness — it only decides how a <c>SuggestCapture</c> is shown:
/// an interactive y/n/v prompt when a real terminal is attached, or a non-blocking
/// "pending, approve later" notice otherwise. AutoCapture and Skip never prompt.
///
/// Hooks and MCP must never reach the interactive path — they pass <c>isInteractive:
/// false</c>, so this never blocks waiting on input.
/// </summary>
public static class InteractiveMemory
{
    /// <summary>A borderline auto-capture (in Ask mode) is one below this confidence with no outcome evidence.</summary>
    private const double StrongConfidence = 0.85;

    /// <summary>How many invalid keystrokes are tolerated before falling back to "pending".</summary>
    private const int MaxInvalidEntries = 3;

    /// <summary>
    /// Renders/handles the capture outcome for a single feedback result. Returns what was
    /// done so the caller can report it. Never throws on user input; never blocks unless
    /// <paramref name="isInteractive"/> is true.
    /// </summary>
    public static async Task<InteractiveMemoryOutcome> HandleAsync(
        FeedbackResult result,
        InteractiveMemoryMode mode,
        bool isInteractive,
        TextReader input,
        TextWriter output,
        IServiceProvider scopedServices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(scopedServices);

        var outcome = result.Decision?.Outcome
            ?? (result.Rule is null ? CaptureOutcome.Skip : CaptureOutcome.AutoCapture);

        switch (outcome)
        {
            case CaptureOutcome.Skip:
                // A skip never asks; the caller shows any skip notice in verbose/activity.
                return result.ReusedExistingRule ? InteractiveMemoryOutcome.ReusedDuplicate : InteractiveMemoryOutcome.Skipped;

            case CaptureOutcome.AutoCapture when result.Rule is { } captured:
                // In Ask mode, downgrade a borderline auto-capture (no outcome evidence,
                // confidence below the strong bar) to a suggestion and ask instead.
                if (mode == InteractiveMemoryMode.Ask && IsBorderline(result))
                {
                    await DemoteToPendingAsync(captured, scopedServices, cancellationToken).ConfigureAwait(false);
                    return await SuggestAsync(result, captured, mode, isInteractive, input, output, scopedServices, cancellationToken)
                        .ConfigureAwait(false);
                }

                return InteractiveMemoryOutcome.AutoCaptured;

            case CaptureOutcome.SuggestCapture when result.Rule is { } pending:
                return await SuggestAsync(result, pending, mode, isInteractive, input, output, scopedServices, cancellationToken)
                    .ConfigureAwait(false);

            default:
                return InteractiveMemoryOutcome.Skipped;
        }
    }

    private static async Task<InteractiveMemoryOutcome> SuggestAsync(
        FeedbackResult result,
        RecallRule rule,
        InteractiveMemoryMode mode,
        bool isInteractive,
        TextReader input,
        TextWriter output,
        IServiceProvider scopedServices,
        CancellationToken cancellationToken)
    {
        // Silent mode and any non-interactive surface (hook, pipe, MCP) never prompt: the
        // rule is already Pending; show the follow-up command and move on.
        if (mode == InteractiveMemoryMode.Silent || !isInteractive)
        {
            WritePendingFollowup(output, rule);
            return InteractiveMemoryOutcome.PendingSuggested;
        }

        WritePrompt(output, result, rule);
        return await PromptLoopAsync(result, rule, input, output, scopedServices, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<InteractiveMemoryOutcome> PromptLoopAsync(
        FeedbackResult result,
        RecallRule rule,
        TextReader input,
        TextWriter output,
        IServiceProvider scopedServices,
        CancellationToken cancellationToken)
    {
        var invalid = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                // Input ended (no terminal answer): fall back to pending, never block.
                WritePendingFollowup(output, rule);
                return InteractiveMemoryOutcome.PendingSuggested;
            }

            switch (line.Trim().ToLowerInvariant())
            {
                case "y" or "yes" or "remember" or "r":
                    return await RememberAsync(rule, output, scopedServices, cancellationToken).ConfigureAwait(false);

                case "n" or "no" or "ignore" or "i":
                    return await IgnoreAsync(rule, output, scopedServices, cancellationToken).ConfigureAwait(false);

                case "v" or "view" or "details" or "d":
                    WriteDetails(output, result, rule);
                    WriteActions(output);
                    continue;

                default:
                    if (++invalid >= MaxInvalidEntries)
                    {
                        output.WriteLine("Too many invalid entries; leaving it as a pending suggestion.");
                        WritePendingFollowup(output, rule);
                        return InteractiveMemoryOutcome.PendingSuggested;
                    }

                    output.WriteLine("Please choose y, n, or v.");
                    continue;
            }
        }
    }

    private static async Task<InteractiveMemoryOutcome> RememberAsync(
        RecallRule rule,
        TextWriter output,
        IServiceProvider scopedServices,
        CancellationToken cancellationToken)
    {
        var lifecycle = scopedServices.GetRequiredService<IRuleLifecycleService>();
        var updated = await lifecycle.ApproveAsync(rule.Id, cancellationToken).ConfigureAwait(false);
        await RecordAsync(ActivityNoticeFactory.ForSuggestionRemembered(updated, "interactive"), scopedServices, cancellationToken)
            .ConfigureAwait(false);
        output.WriteLine($"{ActivityNoticeRenderer.Badge} remembered rule #{updated.Id}.");
        return InteractiveMemoryOutcome.Remembered;
    }

    private static async Task<InteractiveMemoryOutcome> IgnoreAsync(
        RecallRule rule,
        TextWriter output,
        IServiceProvider scopedServices,
        CancellationToken cancellationToken)
    {
        var lifecycle = scopedServices.GetRequiredService<IRuleLifecycleService>();
        var updated = await lifecycle.ArchiveAsync(rule.Id, cancellationToken).ConfigureAwait(false);
        await RecordAsync(ActivityNoticeFactory.ForSuggestionIgnored(updated, "interactive"), scopedServices, cancellationToken)
            .ConfigureAwait(false);
        output.WriteLine($"{ActivityNoticeRenderer.Badge} ignored suggestion #{updated.Id}.");
        return InteractiveMemoryOutcome.Ignored;
    }

    /// <summary>A borderline capture: auto-captured on confidence alone, with no outcome evidence.</summary>
    private static bool IsBorderline(FeedbackResult result) =>
        result.CaptureReason == CaptureReason.None &&
        (result.Decision?.Confidence ?? 1.0) < StrongConfidence;

    private static async Task DemoteToPendingAsync(
        RecallRule rule,
        IServiceProvider scopedServices,
        CancellationToken cancellationToken)
    {
        if (rule.Status == RuleStatus.Pending)
        {
            return;
        }

        var rules = scopedServices.GetRequiredService<IRecallRuleRepository>();
        rule.Status = RuleStatus.Pending;
        await rules.UpdateAsync(rule, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecordAsync(ActivityNotice notice, IServiceProvider scopedServices, CancellationToken cancellationToken) =>
        await scopedServices.GetRequiredService<IActivityRecorder>()
            .RecordAsync(notice, cancellationToken).ConfigureAwait(false);

    // ---- Rendering ------------------------------------------------------------

    private static void WritePrompt(TextWriter output, FeedbackResult result, RecallRule rule)
    {
        output.WriteLine("---");
        output.WriteLine("🚨 possible lesson detected.");
        output.WriteLine();
        output.WriteLine("Candidate:");
        output.WriteLine(rule.RuleText);
        output.WriteLine();
        output.WriteLine("Why:");
        output.WriteLine(WhyLine(result));
        output.WriteLine();
        WriteActions(output);
        output.WriteLine("---");
    }

    private static void WriteActions(TextWriter output)
    {
        output.WriteLine("Actions:");
        output.WriteLine("[y] Remember");
        output.WriteLine("[n] Ignore");
        output.WriteLine("[v] View details");
    }

    private static void WriteDetails(TextWriter output, FeedbackResult result, RecallRule rule)
    {
        output.WriteLine("Details:");
        output.WriteLine($"Rule: {rule.RuleText}");
        if (!string.IsNullOrWhiteSpace(rule.Trigger))
        {
            output.WriteLine($"Trigger: {rule.Trigger}");
        }

        output.WriteLine($"Reason: {result.Decision?.Reason ?? result.Worthiness?.Reason ?? "—"}");
        output.WriteLine($"Confidence: {(result.Decision?.Confidence ?? rule.Confidence):0.00}");
        output.WriteLine($"Evidence: {(string.IsNullOrWhiteSpace(rule.EvidenceSummary) ? "—" : rule.EvidenceSummary)}");
        output.WriteLine($"Scope: {result.Decision?.ScopeLabel ?? rule.ScopeLevel.ToString()}");
        output.WriteLine();
    }

    private static void WritePendingFollowup(TextWriter output, RecallRule rule)
    {
        output.WriteLine($"{ActivityNoticeRenderer.Badge} suggested 1 pending rule.");
        output.WriteLine($"- #{rule.Id} {rule.RuleText}");
        output.WriteLine(
            $"Run `agentrecall rules approve {rule.Id}` to remember it, " +
            $"or `agentrecall rules archive {rule.Id}` to ignore it.");
    }

    private static string WhyLine(FeedbackResult result)
    {
        var reason = result.CaptureReason switch
        {
            CaptureReason.ObservedAgentFailure => "This came from an observed agent mistake, but the rule may be broad.",
            CaptureReason.UserCorrection => "This came from a correction you made, but the rule may be broad.",
            CaptureReason.AcceptedReviewComment => "This came from an accepted review comment, but the rule may be broad.",
            CaptureReason.RepeatedCorrection => "This correction has recurred, but the rule may be broad.",
            CaptureReason.TestFailedThenFixed => "This came from a test that failed then passed, but the rule may be broad.",
            _ => result.Decision?.Notice ?? result.Worthiness?.Reason ?? "The evidence is ambiguous, so confirmation helps.",
        };

        return reason;
    }
}
