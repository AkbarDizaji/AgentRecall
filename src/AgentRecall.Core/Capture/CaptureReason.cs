namespace AgentRecall.Core.Capture;

/// <summary>
/// Why a candidate was captured — the outcome-aware evidence that justified keeping a
/// lesson that text alone might not have warranted. Persisted on the rule (and surfaced
/// by <c>rules explain</c>) so a stored lesson carries the reason it exists, not just
/// its guidance. Deterministic: the same signals always map to the same reason.
/// </summary>
public enum CaptureReason
{
    /// <summary>No outcome-aware evidence; captured (or skipped) on text worthiness alone.</summary>
    None = 0,

    /// <summary>The agent actually made this mistake in the turn (its output broke or changed behaviour).</summary>
    ObservedAgentFailure,

    /// <summary>The user corrected the agent's behaviour ("no, preserve the else branch").</summary>
    UserCorrection,

    /// <summary>An accepted/applied code-review comment.</summary>
    AcceptedReviewComment,

    /// <summary>A test failed and was then fixed, evidencing a real, reproducible mistake.</summary>
    TestFailedThenFixed,

    /// <summary>The same correction was observed two or more times.</summary>
    RepeatedCorrection,

    /// <summary>Surfaced by lesson mining over repeated historical signals.</summary>
    LessonMined,

    /// <summary>Captured from feedback the user supplied directly (the default manual path).</summary>
    ManualFeedback,

    /// <summary>Captured from an external import (a pull request, a failure log).</summary>
    ImportedFeedback,

    /// <summary>
    /// The user explicitly stated a durable preference for how the assistant should
    /// behave or communicate ("answer briefly", "reply in Persian", "give me the
    /// prompt directly"). A first-class acceptance signal: an explicit preference is
    /// captured on the user's word, not inferred from a single message.
    /// </summary>
    ExplicitUserPreference,

    /// <summary>
    /// Installed from a curated built-in seed pack (see <c>agentrecall seed</c>). Not
    /// project-observed evidence: starter guidance the user opted into, carried so a
    /// seed rule explains that it came from a pack rather than a local observation.
    /// </summary>
    BuiltInSeed,

    /// <summary>
    /// The model's own reflection on the turn ("what would I have needed to know upfront?")
    /// surfaced this lesson — no explicit correction or observed failure occurred.
    /// </summary>
    SelfIdentifiedFriction,
}
