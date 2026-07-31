namespace AgentRecall.Core.Capture;

/// <summary>
/// Controls whether AgentRecall <em>asks</em> the user about an ambiguous capture. This
/// is independent of <c>ActivityNoticeLevel</c> (which controls visibility): a mode
/// decides interaction, a notice level decides how loud the recorded notices are.
/// </summary>
public enum InteractiveMemoryMode
{
    /// <summary>
    /// Default. For manual feedback (<c>feedback add</c>), auto-captures high-confidence
    /// lessons silently (a notice only) and asks interactively only for ambiguous
    /// <c>SuggestCapture</c> candidates. For the Stop-hook/semantic-judge capture path, every
    /// would-be auto-capture is parked Pending and surfaced with a yes/no/"yes to all" prompt
    /// instead of being stored immediately — see <see cref="Silent"/> to bypass that. Skips
    /// never ask, in either path.
    /// </summary>
    Auto,

    /// <summary>
    /// More conservative for manual feedback (<c>feedback add</c>): still auto-captures a very
    /// strong signal, but a borderline auto-capture is downgraded to a suggestion and the user
    /// is asked. Behaves exactly like <see cref="Auto"/> for the Stop-hook capture-approval
    /// gate (there is nothing more conservative to add — every capture already requires
    /// approval). Skips never ask.
    /// </summary>
    Ask,

    /// <summary>
    /// Never prompts. Auto-captures per policy and parks suggestions as Pending, but asks
    /// nothing — activity and status commands still show what happened. This is also the
    /// global bypass for the Stop-hook capture-approval gate: with any other mode, a would-be
    /// auto-capture is parked Pending until the user replies yes/no (or "yes to all" for
    /// everything pending in the chat); under <c>Silent</c> it is stored immediately per the
    /// judge's own decision, exactly as before that gate existed.
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
