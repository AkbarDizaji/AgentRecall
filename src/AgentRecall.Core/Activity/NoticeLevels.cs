using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Activity;

/// <summary>
/// Defensive parsing for the configured notice levels. An unrecognised value never
/// crashes startup; it falls back to a safe default so AgentRecall stays usable.
/// </summary>
public static class NoticeLevels
{
    /// <summary>
    /// Resolves a configured notice level. Returns <paramref name="fallback"/> for a
    /// null, empty, or unrecognised value.
    /// </summary>
    public static NoticeLevel Resolve(string? raw, NoticeLevel fallback) =>
        Enum.TryParse<NoticeLevel>(raw, ignoreCase: true, out var level) &&
        Enum.IsDefined(level)
            ? level
            : fallback;

    /// <summary>True when <paramref name="raw"/> is a recognised level (or blank, i.e. default).</summary>
    public static bool IsValid(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ||
        (Enum.TryParse<NoticeLevel>(raw, ignoreCase: true, out var level) && Enum.IsDefined(level));

    /// <summary>
    /// Clamps a level to what a hook may emit. Hook notices must stay compact, so
    /// <see cref="NoticeLevel.Verbose"/> is treated as <see cref="NoticeLevel.Normal"/>.
    /// </summary>
    public static NoticeLevel ClampForHook(NoticeLevel level) =>
        level == NoticeLevel.Verbose ? NoticeLevel.Normal : level;
}
