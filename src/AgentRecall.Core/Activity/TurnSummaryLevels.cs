using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Activity;

/// <summary>
/// Defensive parsing for the configured turn-summary level. An unrecognised value never
/// crashes startup; it falls back to <see cref="Default"/> so AgentRecall stays usable.
/// </summary>
public static class TurnSummaryLevels
{
    /// <summary>The safe default when nothing (or something invalid) is configured.</summary>
    public const TurnSummaryLevel Default = TurnSummaryLevel.Compact;

    /// <summary>Resolves a configured level, falling back to <see cref="Default"/>.</summary>
    public static TurnSummaryLevel Resolve(string? raw) =>
        Enum.TryParse<TurnSummaryLevel>(raw, ignoreCase: true, out var level) && Enum.IsDefined(level)
            ? level
            : Default;

    /// <summary>True when <paramref name="raw"/> is a recognised level (or blank, i.e. default).</summary>
    public static bool IsValid(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ||
        (Enum.TryParse<TurnSummaryLevel>(raw, ignoreCase: true, out var level) && Enum.IsDefined(level));
}
