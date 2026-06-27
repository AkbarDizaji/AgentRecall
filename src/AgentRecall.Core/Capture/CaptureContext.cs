namespace AgentRecall.Core.Capture;

/// <summary>
/// The outcome/context a candidate lesson came out of — the evidence beyond the text
/// itself. The <see cref="IAdaptiveWorthinessPolicy"/> reads these signals to raise or
/// lower the base capture decision, so a generic lesson that names a real agent mistake
/// is kept while the same words with no evidence are skipped.
///
/// Every field is deterministic and produced upstream (the turn finalizer's signal
/// detection, lesson mining's occurrence counts, an importer's source). This type only
/// carries them; it never re-derives them. A <c>null</c> context means "no outcome
/// signals" and the pipeline behaves exactly as before.
/// </summary>
public sealed record CaptureContext
{
    /// <summary>Where the candidate came from (e.g. <c>turn-finalizer</c>, <c>lesson-mining</c>, <c>pr-import</c>).</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The matching <see cref="CaptureReason"/> when the source already knows it (e.g. mining).</summary>
    public CaptureReason Reason { get; init; } = CaptureReason.None;

    /// <summary>True when the user explicitly accepted or asked to keep the guidance.</summary>
    public bool AcceptanceSignal { get; init; }

    /// <summary>True when the agent actually made this mistake (its output broke or changed behaviour).</summary>
    public bool ObservedFailure { get; init; }

    /// <summary>True when the user corrected the agent's behaviour this turn.</summary>
    public bool UserCorrection { get; init; }

    /// <summary>True when an accepted/applied review comment produced this candidate.</summary>
    public bool ReviewAccepted { get; init; }

    /// <summary>True when a test failed and was then fixed, evidencing a reproducible mistake.</summary>
    public bool TestFailedThenFixed { get; init; }

    /// <summary>How many times this same correction was observed (2+ strongly favours capture).</summary>
    public int RepeatedCorrectionCount { get; init; }

    /// <summary>How many prior, similar mistakes are already on record for this guidance.</summary>
    public int PriorSimilarMistakeCount { get; init; }

    /// <summary>True when the user explicitly asked not to save ("do not save this").</summary>
    public bool ExplicitDoNotSave { get; init; }

    /// <summary>True when the user explicitly asked to save ("save this", "remember this").</summary>
    public bool ExplicitSaveRequest { get; init; }

    /// <summary>True when this candidate conflicts with an existing active rule (hold for review).</summary>
    public bool ConflictExists { get; init; }

    /// <summary>The persisted turn finalization this context belongs to, when known.</summary>
    public int? TurnFinalizationId { get; init; }

    /// <summary>A short, human-readable description of the evidence, persisted with the rule.</summary>
    public string? EvidenceSummary { get; init; }

    /// <summary>
    /// True when any outcome-aware evidence is present — an observed failure, a user
    /// correction, an accepted review, a test that failed then passed, or a repeat. This
    /// is what turns a would-be skip of generic advice into a capture.
    /// </summary>
    public bool HasOutcomeEvidence =>
        ObservedFailure || UserCorrection || ReviewAccepted || TestFailedThenFixed ||
        RepeatedCorrectionCount >= 2 || PriorSimilarMistakeCount >= 1;
}
