using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Capture;

/// <summary>
/// The deterministic verdict of the capture pipeline: what AgentRecall decided to do
/// with a candidate once worthiness, confidence, the acceptance signal, duplicate
/// detection and scope have all been weighed. This is the decision that used to be
/// left to the agent ("Want me to save it?"); it now lives inside AgentRecall.
/// </summary>
public enum CaptureOutcome
{
    /// <summary>
    /// Write the rule immediately (Active) and notify the user. The evidence is strong
    /// enough that asking would only add a needless decision.
    /// </summary>
    AutoCapture,

    /// <summary>
    /// Park the rule as a Pending suggestion and ask the user to confirm it. Reserved
    /// for the genuinely ambiguous case where the evidence does not justify acting
    /// without a human nod.
    /// </summary>
    SuggestCapture,

    /// <summary>
    /// Store nothing actionable. Either the candidate is a low-value code fact
    /// (recoverable from the repository) or an equivalent rule already exists.
    /// </summary>
    Skip,
}

/// <summary>
/// The signals fed into <see cref="ICaptureDecisionPolicy"/>. Every field is produced
/// by an existing component (the worthiness classifier, confidence scoring, the
/// acceptance detector, the deduplicator, scope resolution) — the policy only weighs
/// them; it never re-derives them.
/// </summary>
public sealed record CaptureSignals
{
    /// <summary>
    /// True when the candidate is a reusable lesson worth keeping (the worthiness
    /// verdict was WorthStoring or NeedsReview, or screening was disabled).
    /// </summary>
    public required bool Worthy { get; init; }

    /// <summary>Confidence in the candidate, 0.0–1.0 (worthiness or mined confidence).</summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// True when the user explicitly accepted this guidance (an accepted PR comment,
    /// an "apply the review" intent, or an explicit approve). The strongest signal.
    /// </summary>
    public bool ExplicitAcceptance { get; init; }

    /// <summary>
    /// The effective approve posture: the per-call override if given, else the
    /// configured default. False means the deployment (or caller) wants captures
    /// held for review.
    /// </summary>
    public bool ApprovePosture { get; init; }

    /// <summary>True when an equivalent rule already exists for this scope.</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>True when accepted code facts are allowed to bypass the worthiness filter.</summary>
    public bool CodeFactOverrideAllowed { get; init; }

    /// <summary>Scope granularity the candidate applies to.</summary>
    public ScopeLevel ScopeLevel { get; init; }

    /// <summary>Scope identifier (e.g. repo name), if any.</summary>
    public string? ScopeValue { get; init; }

    /// <summary>The worthiness classifier's rationale, surfaced to the user verbatim.</summary>
    public string WorthinessReason { get; init; } = string.Empty;
}

/// <summary>
/// The outcome of the deterministic capture decision, plus everything needed to
/// notify the user without the agent having to improvise: the reason (what kind of
/// knowledge this is), the confidence, the scope, and a notice (why this outcome was
/// reached).
/// </summary>
/// <param name="Outcome">AutoCapture, SuggestCapture, or Skip.</param>
/// <param name="Reason">What the candidate is (the worthiness rationale).</param>
/// <param name="Confidence">Confidence in the candidate, 0.0–1.0.</param>
/// <param name="ScopeLevel">Scope granularity.</param>
/// <param name="ScopeValue">Scope identifier, if any.</param>
/// <param name="Notice">Why this outcome was chosen (e.g. "the acceptance signal was strong").</param>
public sealed record CaptureDecision(
    CaptureOutcome Outcome,
    string Reason,
    double Confidence,
    ScopeLevel ScopeLevel,
    string? ScopeValue,
    string Notice)
{
    /// <summary>A readable scope label, e.g. "Repository:skedda" or "Global".</summary>
    public string ScopeLabel =>
        ScopeLevel == ScopeLevel.Global
            ? "Global"
            : string.IsNullOrWhiteSpace(ScopeValue)
                ? ScopeLevel.ToString()
                : $"{ScopeLevel}:{ScopeValue}";
}

/// <summary>
/// The final, deterministic step of the capture pipeline. Given the signals already
/// produced upstream, it decides whether AgentRecall should auto-capture, suggest, or
/// skip — so the user is almost never asked to make the call. Deterministic and
/// rule-based: same signals in, same decision out. No LLM, no randomness.
/// </summary>
public interface ICaptureDecisionPolicy
{
    /// <summary>Weighs the signals and returns the capture decision.</summary>
    CaptureDecision Decide(CaptureSignals signals);
}
