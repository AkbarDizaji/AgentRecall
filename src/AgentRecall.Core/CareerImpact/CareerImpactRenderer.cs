using System.Text;

namespace AgentRecall.Core.CareerImpact;

/// <summary>
/// Renders a <see cref="CareerImpactAnalysis"/> for humans. Output is deliberately bounded:
/// the compact summary is at most five bullets plus a pointer to the on-demand journal, and
/// even the detailed summary and journal cap their lists — so the automatic end-of-turn
/// summary never becomes a full promotion packet and never bloats the session.
/// </summary>
public static class CareerImpactRenderer
{
    /// <summary>Badge for the automatic career-impact summary and notices.</summary>
    public const string Badge = "🧠 **AgentRecall Career Impact:**";

    /// <summary>The detailed-mode header.</summary>
    public const string DetailedHeader = "🧠 **AgentRecall Career Impact**";

    /// <summary>Message shown when no significant impact was detected for the last turn.</summary>
    public const string NoImpactMessage =
        Badge + " no significant engineering impact detected for the last turn.";

    /// <summary>
    /// Builds the single-line pointer surfaced inside the Turn Memory Summary. <paramref name="hint"/>
    /// is the detector's top reason (e.g. "involves a migration") and is parenthesized onto the
    /// pointer when present, so the summary hints at what triggered it without repeating the full
    /// career-impact detail.
    /// </summary>
    public static string BuildTurnSummaryPointer(string? hint)
    {
        var suffix = string.IsNullOrWhiteSpace(hint) ? "" : $" ({hint})";
        return $"possible Staff-level impact detected{suffix}; run `agentrecall career journal --last`";
    }

    /// <summary>Compact one-block summary: at most five bullets plus a journal pointer.</summary>
    public static string RenderCompact(CareerImpactAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var bullets = new List<string>();
        if (!string.IsNullOrWhiteSpace(analysis.WhyThisMatters))
        {
            bullets.Add($"Why it matters: {analysis.WhyThisMatters}");
        }

        if (analysis.SuggestedEvidence.Count > 0)
        {
            bullets.Add($"Evidence: {string.Join(", ", analysis.SuggestedEvidence)}");
        }

        if (analysis.SuggestedMetrics.Count > 0)
        {
            bullets.Add($"Metrics: {string.Join(", ", analysis.SuggestedMetrics)}");
        }

        if (analysis.Stakeholders.Count > 0)
        {
            bullets.Add($"Stakeholders: {string.Join(", ", analysis.Stakeholders)}");
        }

        bullets.Add($"ADR: {(analysis.Adr.Recommended ? "probably yes" : "not needed")}");

        var sb = new StringBuilder();
        sb.Append(Badge).Append(' ').Append(analysis.CompactSummary).Append('.');
        foreach (var bullet in bullets.Take(5))
        {
            sb.Append("\n- ").Append(bullet);
        }

        sb.Append("\n\nRun `agentrecall career journal --last` for a promotion-ready entry.");
        return sb.ToString();
    }

    /// <summary>Detailed but bounded summary: impact / evidence / metrics / stakeholders / ADR / promotion.</summary>
    public static string RenderDetailed(CareerImpactAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var sb = new StringBuilder();
        sb.Append(DetailedHeader);

        AppendLine(sb, "Why this matters", analysis.WhyThisMatters);
        AppendLine(sb, "Technical impact", analysis.TechnicalImpact);
        AppendLine(sb, "Business/user impact", analysis.BusinessImpact);
        AppendLine(sb, "Long-term impact", analysis.LongTermImpact);
        AppendItems(sb, "Evidence to collect", analysis.SuggestedEvidence);
        AppendItems(sb, "Metrics", analysis.SuggestedMetrics);
        AppendItems(sb, "Stakeholders", analysis.Stakeholders);

        sb.Append("\n\nADR:");
        sb.Append("\n- Suggested: ").Append(analysis.Adr.Recommended ? "yes" : "no");
        if (analysis.Adr.Recommended && !string.IsNullOrWhiteSpace(analysis.Adr.SuggestedTitle))
        {
            sb.Append("\n- Title: ").Append(analysis.Adr.SuggestedTitle);
        }

        AppendLine(sb, "Promotion note", analysis.PromotionNote);

        sb.Append("\n\nRun `agentrecall career journal --last` for a promotion-ready entry.");
        return sb.ToString();
    }

    /// <summary>A promotion-ready career journal entry in Markdown (generated only on demand).</summary>
    public static string RenderJournal(CareerImpactAnalysis analysis, DateTimeOffset date)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var sb = new StringBuilder();
        sb.Append("# Career Journal Entry\n\n");
        sb.Append("Date:\n").Append(date.ToString("yyyy-MM-dd")).Append("\n\n");
        sb.Append("Work:\n").Append(Fallback(analysis.WhyThisMatters, "Significant engineering work advanced this turn.")).Append("\n\n");
        sb.Append("Impact:\n").Append(Fallback(analysis.TechnicalImpact, "Meaningful technical impact.")).Append('\n');
        if (!string.IsNullOrWhiteSpace(analysis.BusinessImpact))
        {
            sb.Append(analysis.BusinessImpact).Append('\n');
        }

        sb.Append('\n');
        AppendJournalList(sb, "Evidence", analysis.SuggestedEvidence);
        AppendJournalList(sb, "Metrics", analysis.SuggestedMetrics);
        AppendJournalList(sb, "Stakeholders", analysis.Stakeholders);
        AppendJournalList(sb, "Leadership / Staff behaviors", LeadershipBehaviors(analysis));

        sb.Append("ADR:\n");
        if (analysis.Adr.Recommended)
        {
            sb.Append("Suggested: yes — ").Append(Fallback(analysis.Adr.SuggestedTitle, "Record the architecture decision")).Append('\n');
        }
        else
        {
            sb.Append("Not required.\n");
        }

        sb.Append('\n');
        sb.Append("Promotion category:\n").Append(PromotionCategory(analysis)).Append("\n\n");
        sb.Append("Promotion-ready achievement:\n").Append(Fallback(analysis.PromotionNote, "Advanced meaningful engineering work.")).Append("\n\n");
        sb.Append("Next action:\n").Append(NextAction(analysis)).Append('\n');
        return sb.ToString();
    }

    private static IReadOnlyList<string> LeadershipBehaviors(CareerImpactAnalysis analysis)
    {
        var behaviors = analysis.Categories
            .Where(c => c is Domain.ImpactCategory.Leadership
                or Domain.ImpactCategory.CrossTeamImpact
                or Domain.ImpactCategory.ProcessImprovement
                or Domain.ImpactCategory.LongTermLeverage)
            .Select(c => c.ToString())
            .ToList();
        return behaviors.Count > 0 ? behaviors : ["Individual technical contribution"];
    }

    private static string PromotionCategory(CareerImpactAnalysis analysis) =>
        analysis.Categories.Count > 0
            ? string.Join(", ", analysis.Categories.Take(3).Select(c => c.ToString()))
            : "Engineering impact";

    private static string NextAction(CareerImpactAnalysis analysis) =>
        analysis.NextActions.Count > 0 ? analysis.NextActions[0] : "Collect evidence while the work is fresh.";

    private static void AppendLine(StringBuilder sb, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        sb.Append("\n\n").Append(label).Append(":\n- ").Append(value);
    }

    private static void AppendItems(StringBuilder sb, string label, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        sb.Append("\n\n").Append(label).Append(':');
        foreach (var item in items.Take(6))
        {
            sb.Append("\n- ").Append(item);
        }
    }

    private static void AppendJournalList(StringBuilder sb, string label, IReadOnlyList<string> items)
    {
        sb.Append(label).Append(":\n");
        if (items.Count == 0)
        {
            sb.Append("- (none)\n\n");
            return;
        }

        foreach (var item in items.Take(6))
        {
            sb.Append("* ").Append(item).Append('\n');
        }

        sb.Append('\n');
    }

    private static string Fallback(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
