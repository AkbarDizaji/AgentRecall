using System.Text;
using AgentRecall.Core.Context;

namespace AgentRecall.Core.Hooks;

/// <summary>
/// Renders an injected-context result into a compact, structured block for the
/// UserPromptSubmit hook. Empty sections are dropped to minimise token overhead;
/// when nothing is relevant it returns an empty string (so nothing is injected).
/// </summary>
public static class HookContextFormatter
{
    private const int MaxItemLength = 200;

    public static string Format(ContextInjectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.All.Any())
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## AgentRecall Technical Context");

        // Each section renders its rules as conditional blocks (When / Do / Avoid /
        // Because) so the agent receives knowledge in the same shape it is stored.
        AppendConditionalSection(sb, "Must Follow", result.MustFollow);
        AppendConditionalSection(sb, "Warnings", result.Warnings);
        AppendConditionalSection(sb, "Preferred Patterns", result.Suggested);

        // A compact source list stays at the end so the agent can cite every rule.
        AppendSourceRules(sb, result.All.Select(r => $"#{r.Rule.Id}"));

        // The retrieval id these rules were recorded under. It is the handle an outcome
        // attaches to, and the agent is the only party that can report one — so the id has to
        // travel with the rules. Without it, every outcome AgentRecall stores would have to
        // guess which retrieval it belonged to, and the confidence ledger would stay empty.
        if (!string.IsNullOrWhiteSpace(result.RetrievalId))
        {
            sb.AppendLine();
            sb.AppendLine($"Retrieval id: {result.RetrievalId} (report rule outcomes against this id)");
        }

        // Surface a conflict only when resolution changed what was injected.
        if (result.Conflicts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Conflicts.ConflictRenderer.Hint);
            sb.AppendLine();
            sb.AppendLine(Conflicts.ConflictRenderer.Section(result.Conflicts));
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendConditionalSection(StringBuilder sb, string title, IReadOnlyList<Context.InjectedRule> rules)
    {
        if (rules.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"{title}:");
        foreach (var injected in rules)
        {
            // The block's own lines are indented; the "- " bullet sits in front of
            // the condition so the Do/Avoid/Because lines nest beneath it.
            var block = Context.ConditionalRuleFormatter.Format(injected.Rule, indent: 2, includeSource: true);
            var lines = block.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
            sb.AppendLine($"- {Truncate(lines[0], MaxItemLength)}");
            for (var i = 1; i < lines.Length; i++)
            {
                sb.AppendLine($"  {Truncate(lines[i], MaxItemLength)}");
            }
        }
    }

    private static void AppendSourceRules(StringBuilder sb, IEnumerable<string> ids)
    {
        var list = ids
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i.Trim())
            .Distinct()
            .ToList();

        if (list.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("Source Rules:");
        foreach (var item in list)
        {
            sb.AppendLine($"- {item}");
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
