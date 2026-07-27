using System.Text;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Summary;

/// <summary>
/// Renders a <see cref="TurnSummary"/> for humans. Output is bounded: each section shows at
/// most <see cref="MaxItems"/> entries and only short titles, never full rule bodies, so the
/// summary stays small enough to surface in a hook without bloating the session.
/// </summary>
public static class TurnSummaryRenderer
{
    /// <summary>The shared AgentRecall badge for the compact one-liner.</summary>
    public const string Badge = ActivityNoticeRenderer.Badge;

    /// <summary>The detailed-mode header.</summary>
    public const string DetailedHeader = "🧠 **AgentRecall Turn Summary**";

    /// <summary>Per-section display cap, keeping even detailed output bounded.</summary>
    public const int MaxItems = 5;

    /// <summary>
    /// Renders the summary at the given level. Returns null for
    /// <see cref="TurnSummaryLevel.Silent"/> so callers emit nothing.
    /// <paramref name="emptyScope"/> phrases the no-activity message (e.g. "this turn"
    /// for the hook, "the last turn" for the CLI).
    /// </summary>
    public static string? Render(TurnSummary summary, TurnSummaryLevel level, string emptyScope = "this turn")
    {
        ArgumentNullException.ThrowIfNull(summary);
        return level switch
        {
            TurnSummaryLevel.Silent => null,
            TurnSummaryLevel.Detailed => RenderDetailed(summary, emptyScope),
            _ => RenderCompact(summary, emptyScope),
        };
    }

    /// <summary>The one-line aggregate: used / auto-captured / suggested / skipped counts.</summary>
    public static string RenderCompact(TurnSummary summary, string emptyScope = "this turn")
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.IsEmpty)
        {
            return $"{Badge} no memory activity recorded for {emptyScope}.";
        }

        var line = new StringBuilder();
        line.Append(Badge)
            .Append(" used ").Append(summary.Used.Count).Append(summary.Used.Count == 1 ? " rule" : " rules")
            .Append("; auto-captured ").Append(summary.Captured.Count)
            .Append(", suggested ").Append(summary.Suggested.Count)
            .Append(", skipped ").Append(summary.Skipped.Count);

        if (summary.Errors.Count > 0)
        {
            line.Append(", errors ").Append(summary.Errors.Count);
        }

        line.Append('.');

        foreach (var error in summary.Errors.Take(MaxItems))
        {
            line.Append("\n- Error: ").Append(error);
        }

        if (!string.IsNullOrWhiteSpace(summary.CareerImpact))
        {
            line.Append("\nCareer Impact:\n- ").Append(summary.CareerImpact);
        }

        if (!string.IsNullOrWhiteSpace(summary.DocOpportunity))
        {
            line.Append("\nDocument Opportunity:\n- ").Append(summary.DocOpportunity);
        }

        return line.ToString();
    }

    /// <summary>Grouped sections with short titles and reasons; bounded to <see cref="MaxItems"/>.</summary>
    public static string RenderDetailed(TurnSummary summary, string emptyScope = "this turn")
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.IsEmpty)
        {
            return $"{Badge} no memory activity recorded for {emptyScope}.";
        }

        var sb = new StringBuilder();
        sb.Append(DetailedHeader);

        // The core four are always shown (with "- none" when empty) so the summary
        // proactively answers "did it use / auto-capture / suggest / skip anything?".
        AppendRuleSection(sb, "Used", summary.Used, withApprove: false, alwaysShow: true);
        AppendRuleSection(sb, "Auto-captured", summary.Captured, withApprove: false, alwaysShow: true);
        AppendRuleSection(sb, "Suggested", summary.Suggested, withApprove: true, alwaysShow: true);
        AppendSkipSection(sb, summary.Skipped);

        // Interactive and error sections appear only when there is something to report.
        AppendRuleSection(sb, "Remembered", summary.Remembered, withApprove: false, alwaysShow: false);
        AppendRuleSection(sb, "Ignored", summary.Ignored, withApprove: false, alwaysShow: false);
        AppendErrorSection(sb, summary.Errors);

        // Only a short pointer to the on-demand journal — never the full career summary.
        if (!string.IsNullOrWhiteSpace(summary.CareerImpact))
        {
            sb.Append("\n\nCareer Impact:\n- ").Append(summary.CareerImpact);
        }

        // Only a short pointer — never the reason or key points — until the user agrees.
        if (!string.IsNullOrWhiteSpace(summary.DocOpportunity))
        {
            sb.Append("\n\nDocument Opportunity:\n- ").Append(summary.DocOpportunity);
        }

        return sb.ToString();
    }

    private static void AppendRuleSection(
        StringBuilder sb,
        string title,
        IReadOnlyList<TurnSummaryRule> rules,
        bool withApprove,
        bool alwaysShow)
    {
        if (rules.Count == 0 && !alwaysShow)
        {
            return;
        }

        sb.Append("\n\n").Append(title).Append(':');
        if (rules.Count == 0)
        {
            sb.Append("\n- none");
            return;
        }

        foreach (var rule in rules.Take(MaxItems))
        {
            sb.Append("\n- #").Append(rule.Id).Append(' ').Append(rule.Title);
            if (rule.Seed)
            {
                sb.Append(" [seed]");
            }

            if (rule.Standing)
            {
                sb.Append(" [standing]");
            }

            if (withApprove)
            {
                sb.Append("\n  Approve: `agentrecall rules approve ").Append(rule.Id).Append('`');
            }
        }

        AppendOverflow(sb, rules.Count);
    }

    private static void AppendSkipSection(StringBuilder sb, IReadOnlyList<TurnSummarySkip> skips)
    {
        sb.Append("\n\nSkipped:");
        if (skips.Count == 0)
        {
            sb.Append("\n- none");
            return;
        }

        foreach (var skip in skips.Take(MaxItems))
        {
            sb.Append("\n- ");
            if (!string.IsNullOrWhiteSpace(skip.Title))
            {
                sb.Append(skip.Title).Append(": ");
            }

            sb.Append(skip.Reason);
        }

        AppendOverflow(sb, skips.Count);
    }

    private static void AppendErrorSection(StringBuilder sb, IReadOnlyList<string> errors)
    {
        if (errors.Count == 0)
        {
            return;
        }

        sb.Append("\n\nErrors:");
        foreach (var error in errors.Take(MaxItems))
        {
            sb.Append("\n- ").Append(error);
        }

        AppendOverflow(sb, errors.Count);
    }

    private static void AppendOverflow(StringBuilder sb, int total)
    {
        if (total > MaxItems)
        {
            sb.Append("\n- …and ").Append(total - MaxItems).Append(" more");
        }
    }
}
