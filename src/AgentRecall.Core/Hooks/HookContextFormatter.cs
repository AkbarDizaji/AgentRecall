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

        // The heading carries the running build's contract stamp. Instructions outlive the binary
        // that has to honour them — hooks run the installed tool, not the working tree — so the
        // agent needs to see which build actually answered, and the heading is the one line that
        // is always there. A missing or lower stamp than the instructions declare is the tell that
        // the installed CLI predates them; see AgentContract.
        sb.AppendLine($"## AgentRecall Technical Context ({AgentContract.Stamp})");

        // Each section renders its rules as conditional blocks (When / Do / Avoid /
        // Because) so the agent receives knowledge in the same shape it is stored.
        AppendConditionalSection(sb, "Must Follow", result.MustFollow, Context.RuleDetail.Full);
        AppendConditionalSection(sb, "Warnings", result.Warnings, Context.RuleDetail.Full);
        AppendConditionalSection(sb, "Preferred Patterns", result.Suggested, Context.RuleDetail.Compact);

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

    private static void AppendConditionalSection(
        StringBuilder sb,
        string title,
        IReadOnlyList<Context.InjectedRule> rules,
        Context.RuleDetail detail)
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
            // the condition so the Do/Avoid/Because lines nest beneath it. A rule this chat has
            // already read carries its own detail, set when it was costed against the budget.
            var block = Context.ConditionalRuleFormatter.Format(
                injected.Rule,
                indent: 2,
                includeSource: true,
                detail: injected.Detail == Context.RuleDetail.Reminder ? Context.RuleDetail.Reminder : detail);
            var lines = block.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
            sb.AppendLine($"- {Truncate(lines[0], MaxItemLength)}");
            for (var i = 1; i < lines.Length; i++)
            {
                sb.AppendLine($"  {Truncate(lines[i], MaxItemLength)}");
            }
        }
    }

    /// <summary>
    /// Trims to the last sentence end, or failing that the last word, inside the cap. Cutting
    /// mid-word spends the whole allowance on text that stops in the middle of a thought: the
    /// tokens are paid for either way, so they may as well end somewhere a reader can act on.
    /// </summary>
    private static string Truncate(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        var window = value[..(max - 1)];

        var sentence = window.LastIndexOfAny(['.', '!', '?', ';', ':']);
        if (sentence >= max / 2)
        {
            return window[..(sentence + 1)] + " …";
        }

        var word = window.LastIndexOf(' ');
        return (word >= max / 2 ? window[..word] : window) + " …";
    }
}
