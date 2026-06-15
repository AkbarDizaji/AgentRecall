namespace AgentRecall.Core.Context;

/// <summary>
/// Projects a <see cref="ContextInjectionResult"/> into the agent-optimized views
/// that callers (CLI / MCP) surface: preferred patterns, anti-patterns, and the
/// source rule ids. Shared so every entry point reports the same thing.
/// </summary>
public static class ContextProjection
{
    /// <summary>Ids of every rule included in the context, highest priority first.</summary>
    public static IReadOnlyList<int> SourceRuleIds(ContextInjectionResult result) =>
        result.All.Select(r => r.Rule.Id).Distinct().ToList();

    /// <summary>Positive directives to follow, drawn from must-follow and suggested rules.</summary>
    public static IReadOnlyList<string> PreferredPatterns(ContextInjectionResult result) =>
        result.MustFollow.Concat(result.Suggested)
            .Select(r => r.Rule.RuleText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .ToList();

    /// <summary>Things to avoid: warning rules plus the recorded "do not" of any rule.</summary>
    public static IReadOnlyList<string> AntiPatterns(ContextInjectionResult result)
    {
        var fromWarnings = result.Warnings.Select(r => r.Rule.RuleText);
        var fromMistakes = result.All.Select(r => r.Rule.Mistake);

        return fromWarnings.Concat(fromMistakes)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .ToList();
    }
}
