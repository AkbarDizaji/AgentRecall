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

        AppendSection(sb, "Must Follow", result.MustFollow.Select(r => r.Rule.RuleText));
        AppendSection(sb, "Warnings", result.Warnings.Select(r => r.Rule.RuleText));
        AppendSection(sb, "Preferred Patterns", result.Suggested.Select(r => r.Rule.RuleText));
        AppendSection(sb, "Anti Patterns", result.All.Select(r => r.Rule.Mistake));
        AppendSection(sb, "Source Rules", result.All.Select(r => $"#{r.Rule.Id}"));

        return sb.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder sb, string title, IEnumerable<string> items)
    {
        var list = items
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i.Trim())
            .Distinct()
            .ToList();

        if (list.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"{title}:");
        foreach (var item in list)
        {
            sb.AppendLine($"- {Truncate(item, MaxItemLength)}");
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
