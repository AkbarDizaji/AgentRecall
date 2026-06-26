using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Activity;

/// <summary>
/// An in-flight activity notice: the plain-text summary and optional detail that an
/// integration point produces. It is rendered for humans (with the AgentRecall
/// badge, optionally with detail bullets) and persisted as an
/// <see cref="AgentRecallActivity"/>. Summary and detail are deliberately
/// Markdown-free so the renderer is the single place that adds styling.
/// </summary>
public sealed record ActivityNotice
{
    public required ActivityType Type { get; init; }

    /// <summary>The concise one-line summary (plain text, no emoji or Markdown).</summary>
    public required string Summary { get; init; }

    /// <summary>Verbose detail lines (plain text, no leading bullet). Empty when none.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    public IReadOnlyList<int> RuleIds { get; init; } = [];
    public IReadOnlyList<int> CandidateIds { get; init; } = [];
    public IReadOnlyList<int> RecommendationIds { get; init; } = [];

    /// <summary>Where the activity originated (e.g. "cli", "hook", "mcp").</summary>
    public string Source { get; init; } = "cli";

    /// <summary>Optional stable identity used to deduplicate repeated operations.</summary>
    public string? OperationHash { get; init; }

    /// <summary>Reconstructs a notice from a persisted activity for re-rendering.</summary>
    public static ActivityNotice FromEntity(AgentRecallActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return new ActivityNotice
        {
            Type = activity.ActivityType,
            Summary = activity.Summary,
            Details = SplitLines(activity.Details),
            RuleIds = ParseIds(activity.RuleIds),
            CandidateIds = ParseIds(activity.CandidateIds),
            RecommendationIds = ParseIds(activity.RecommendationIds),
            Source = activity.Source,
            OperationHash = activity.OperationHash,
        };
    }

    private static IReadOnlyList<string> SplitLines(string? value) =>
        string.IsNullOrEmpty(value)
            ? []
            : value.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyList<int> ParseIds(string? value) =>
        string.IsNullOrEmpty(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : (int?)null)
                .Where(n => n is not null)
                .Select(n => n!.Value)
                .ToList();
}
