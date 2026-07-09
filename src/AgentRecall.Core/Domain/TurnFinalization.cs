namespace AgentRecall.Core.Domain;

/// <summary>
/// A record of one turn finalization: what AgentRecall extracted from a completed
/// turn and decided to capture, suggest, or skip. Persisted so the agent can query
/// the last finalization result instead of guessing whether the Stop hook captured
/// anything.
///
/// Privacy by default: the raw turn transcript is not stored unless
/// <see cref="Configuration.AgentRecallOptions.StoreTurnTranscript"/> is enabled.
/// Otherwise only a content hash, the resulting rule ids, and skip reasons are kept.
/// </summary>
public sealed class TurnFinalization
{
    public int Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The working directory the turn ran in (used to scope the lookup).</summary>
    public string Cwd { get; set; } = string.Empty;

    /// <summary>Where the finalization was triggered from (e.g. <c>stop_hook</c>, <c>manual</c>).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Comma-separated ids of rules captured (Active) on this turn.</summary>
    public string CapturedRuleIds { get; set; } = string.Empty;

    /// <summary>Comma-separated ids of rules parked as Pending suggestions on this turn.</summary>
    public string SuggestedRuleIds { get; set; } = string.Empty;

    /// <summary>Newline-separated reasons candidates were skipped.</summary>
    public string SkippedReasons { get; set; } = string.Empty;

    /// <summary>Comma-separated ids of existing rules a candidate duplicated.</summary>
    public string DuplicateRuleIds { get; set; } = string.Empty;

    /// <summary>A short summary of any non-fatal errors encountered while finalizing.</summary>
    public string ErrorSummary { get; set; } = string.Empty;

    /// <summary>Stable hash of the turn content, used to make finalization idempotent.</summary>
    public string RawHash { get; set; } = string.Empty;

    /// <summary>
    /// Deterministic turn correlation id derived from the working directory and prompt
    /// (see <see cref="Activity.TurnCorrelation"/>). Distinct from <see cref="RawHash"/>
    /// (which also folds in the assistant response for idempotency): this id is shared
    /// with the retrieval activity recorded at UserPromptSubmit so a turn's captures and
    /// the rules it used can be joined into one summary. Empty when no prompt was present.
    /// </summary>
    public string TurnId { get; set; } = string.Empty;

    /// <summary>
    /// The raw turn transcript, stored only when transcript persistence is enabled;
    /// otherwise empty.
    /// </summary>
    public string Transcript { get; set; } = string.Empty;

    /// <summary>
    /// What decided this turn's capture — <c>SemanticCaptureJudge</c> when the judge produced a
    /// verdict, or empty when the judge was unavailable. Persisted so <c>capture-status</c> and
    /// <c>turn-summary</c> can report the decision source after the turn.
    /// </summary>
    public string DecisionSource { get; set; } = string.Empty;

    /// <summary>The judge's decision name (Capture/SuggestCapture/Skip/…), or empty.</summary>
    public string JudgeDecision { get; set; } = string.Empty;

    /// <summary>
    /// The judge's exact capture reason name (e.g. <c>ReviewerCorrection</c>,
    /// <c>SourceDocumentOnly</c>). Stored as a string because the judge's reason vocabulary is
    /// richer than the domain <see cref="Capture.CaptureReason"/>.
    /// </summary>
    public string JudgeCaptureReason { get; set; } = string.Empty;

    /// <summary>The judge's confidence for the decision.</summary>
    public double JudgeConfidence { get; set; }
}
