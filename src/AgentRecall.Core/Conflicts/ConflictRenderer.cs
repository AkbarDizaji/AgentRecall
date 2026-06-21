using System.Text;

namespace AgentRecall.Core.Conflicts;

/// <summary>
/// Renders resolved conflicts into a concise, token-light block for retrieval
/// output, plus the one-line hint shown when resolution changed what was injected.
/// </summary>
public static class ConflictRenderer
{
    /// <summary>The short hint surfaced when a conflict affected the current task.</summary>
    public const string Hint = "AgentRecall detected conflicting rules and selected the most applicable one.";

    private const int MaxRuleTextLength = 90;

    /// <summary>Renders one or more resolved conflicts. Returns empty when there are none.</summary>
    public static string Section(IReadOnlyList<ResolvedConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        if (conflicts.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < conflicts.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine();
            }

            AppendConflict(sb, conflicts[i]);
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendConflict(StringBuilder sb, ResolvedConflict resolved)
    {
        var selected = resolved.Selected;
        var ignored = resolved.Ignored;
        var others = string.Join(", ", ignored.Select(r => $"#{r.Id}"));

        sb.AppendLine("Conflict Detected:");
        sb.AppendLine($"- Rule #{selected.Id} conflicts with Rule {others}.");
        sb.AppendLine();

        sb.AppendLine("Selected:");
        sb.AppendLine($"- #{selected.Id} {Truncate(selected.RuleText)}");
        sb.AppendLine();

        sb.AppendLine("Why:");
        foreach (var reason in resolved.Resolution.Explanation)
        {
            sb.AppendLine($"- {reason}");
        }

        sb.AppendLine();
        sb.AppendLine("Ignored:");
        var scoreById = resolved.Resolution.ScoreBreakdown.ToDictionary(s => s.RuleId);
        foreach (var rule in ignored)
        {
            sb.AppendLine($"- #{rule.Id} {IgnoredReason(scoreById, selected.Id, rule.Id)}");
        }
    }

    private static string IgnoredReason(IReadOnlyDictionary<int, RuleScore> scores, int selectedId, int ignoredId)
    {
        if (!scores.TryGetValue(selectedId, out var sel) || !scores.TryGetValue(ignoredId, out var ig))
        {
            return "because the selected rule scored higher.";
        }

        var reasons = new List<string>();
        if (sel.ScopeSpecificity > ig.ScopeSpecificity)
        {
            reasons.Add("broader");
        }

        if (sel.Confidence > ig.Confidence)
        {
            reasons.Add("lower confidence");
        }

        if (sel.StatusWeight > ig.StatusWeight)
        {
            reasons.Add("weaker status");
        }

        if (sel.TriggerSpecificity > ig.TriggerSpecificity)
        {
            reasons.Add("less specific");
        }

        return reasons.Count > 0
            ? $"because it is {string.Join(" and ", reasons)}."
            : "because the selected rule scored higher.";
    }

    private static string Truncate(string value) =>
        value.Length <= MaxRuleTextLength ? value : value[..(MaxRuleTextLength - 1)] + "…";
}
