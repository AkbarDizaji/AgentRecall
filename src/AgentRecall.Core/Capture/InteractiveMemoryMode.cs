namespace AgentRecall.Core.Capture;

/// <summary>
/// Controls whether AgentRecall <em>asks</em> the user about an ambiguous capture. This
/// is independent of <c>ActivityNoticeLevel</c> (which controls visibility): a mode
/// decides interaction, a notice level decides how loud the recorded notices are.
/// </summary>
public enum InteractiveMemoryMode
{
    /// <summary>
    /// Default. Auto-capture high-confidence lessons silently (a notice only) and ask
    /// interactively only for ambiguous <c>SuggestCapture</c> candidates. Skips never ask.
    /// </summary>
    Auto,

    /// <summary>
    /// More conservative. Still auto-captures a very strong signal, but a borderline
    /// auto-capture is downgraded to a suggestion and the user is asked. Skips never ask.
    /// </summary>
    Ask,

    /// <summary>
    /// Never prompts. Auto-captures per policy and parks suggestions as Pending, but asks
    /// nothing — activity and status commands still show what happened.
    /// </summary>
    Silent,
}

/// <summary>
/// Defensive parsing for <see cref="InteractiveMemoryMode"/>. An unrecognised configured
/// value never crashes startup; it falls back to the safe default.
/// </summary>
public static class InteractiveMemoryModes
{
    /// <summary>The safe default when nothing (or something invalid) is configured.</summary>
    public const InteractiveMemoryMode Default = InteractiveMemoryMode.Auto;

    /// <summary>Resolves a configured mode, falling back to <see cref="Default"/>.</summary>
    public static InteractiveMemoryMode Resolve(string? raw) =>
        Enum.TryParse<InteractiveMemoryMode>(raw, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : Default;

    /// <summary>True when <paramref name="raw"/> is a recognised mode (or blank, i.e. default).</summary>
    public static bool IsValid(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ||
        (Enum.TryParse<InteractiveMemoryMode>(raw, ignoreCase: true, out var mode) && Enum.IsDefined(mode));
}
