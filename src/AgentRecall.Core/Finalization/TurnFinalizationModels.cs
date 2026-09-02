using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Finalization;

/// <summary>
/// The completed turn AgentRecall finalizes. The CLI resolves the user prompt and
/// assistant response from the Stop-hook payload (inline fields or a transcript) and
/// derives the repository scope, so the finalizer itself stays pure and deterministic
/// (no file IO, no network, no LLM).
/// </summary>
public sealed record TurnFinalizationInput
{
    /// <summary>The working directory the turn ran in, if known.</summary>
    public string? Cwd { get; init; }

    /// <summary>The latest user message in the turn (a correction, request, or question).</summary>
    public string? Prompt { get; init; }

    /// <summary>The assistant's response text for the turn, if available.</summary>
    public string? AssistantResponse { get; init; }

    /// <summary>Where finalization was triggered from (e.g. <c>stop_hook</c>, <c>manual</c>).</summary>
    public string Source { get; init; } = "manual";

    /// <summary>An explicit acceptance signal from the payload, when the host provides one.</summary>
    public bool? Accepted { get; init; }

    /// <summary>Scope granularity captured rules apply to (derived from the repository).</summary>
    public ScopeLevel ScopeLevel { get; init; } = ScopeLevel.Global;

    /// <summary>Scope identifier (e.g. repository name), if any.</summary>
    public string? ScopeValue { get; init; }

    /// <summary>The raw transcript, stored only when transcript persistence is enabled.</summary>
    public string? RawTranscript { get; init; }

    /// <summary>
    /// The host's conversation/session id, when supplied (e.g. Claude Code's Stop-hook
    /// <c>session_id</c>). Stamped onto any rule this turn parks pending approval, so
    /// "yes to all" can later resolve every rule awaiting approval in this same chat.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// The semantic judge's verdict for the turn, when the host supplied it on the payload. The
    /// default host-supplied judge returns exactly this; a <c>null</c> verdict means the judge is
    /// unavailable and the turn is skipped (never keyword-captured).
    /// </summary>
    public Capture.Judge.CaptureJudgeVerdict? SuppliedJudgment { get; init; }

    /// <summary>
    /// The document-opportunity judge's verdict for the turn, when the host supplied it on the
    /// payload. A <c>null</c> verdict means the judge is unavailable and nothing is offered.
    /// </summary>
    public Capture.Judge.DocOpportunityVerdict? SuppliedDocOpportunity { get; init; }

    /// <summary>
    /// How the rules this turn injected actually fared, when the host reported it. AgentRecall
    /// cannot observe this itself, and never infers it: an empty list means nobody said, which is
    /// recorded as unreported rather than as "the rules did nothing".
    /// </summary>
    public IReadOnlyList<Outcomes.ReportedRuleOutcome> RuleOutcomes { get; init; } = [];

    /// <summary>
    /// True when the host signalled that this end-of-turn is already the resumption of one it
    /// blocked earlier (Claude Code's historical <c>stop_hook_active</c>). The current hook payload
    /// does not document such a field, so enforcement does not depend on it: the persisted attempt
    /// counter is the loop guard, and this flag only ever makes AgentRecall ask less.
    /// </summary>
    public bool HostResumedTurn { get; init; }

    /// <summary>
    /// Set by the Stop-hook path when it already asked the model for this turn's judgment, the turn
    /// came back without one, and the allowed asks ran out. It only sharpens the recorded reason:
    /// "asked and not answered" instead of "never judged". It never enables an alternative capture
    /// route — an unjudged turn stores nothing either way.
    /// </summary>
    public bool JudgmentRequestExhausted { get; init; }
}

/// <summary>A lesson AgentRecall captured or suggested for the turn.</summary>
public sealed record FinalizedLesson
{
    public required int RuleId { get; init; }
    public required RuleCategory Category { get; init; }
    public required string Text { get; init; }
    public required string ScopeLabel { get; init; }
    public double Confidence { get; init; }

    /// <summary>
    /// True when the rule is a standing (always-apply) universal constraint, surfaced so the
    /// user sees the correction became a rule that applies on every turn.
    /// </summary>
    public bool AlwaysApply { get; init; }

    /// <summary>An optional note (e.g. why it was suggested rather than captured).</summary>
    public string? Note { get; init; }

    /// <summary>
    /// True when the rule is stored <see cref="RuleStatus.Pending"/> and needs the user's
    /// yes/no (or "yes to all") before it counts as approved memory — either because the
    /// judge itself was only confident enough to suggest it, or because it would have been
    /// auto-captured but the default approval gate parked it instead.
    /// </summary>
    public bool AwaitingApproval { get; init; }
}

/// <summary>A candidate AgentRecall stored nothing for, with the reason.</summary>
public sealed record SkippedLesson
{
    public required string Reason { get; init; }

    /// <summary>The id of the existing rule this candidate duplicated, if a duplicate.</summary>
    public int? DuplicateOfRuleId { get; init; }

    /// <summary>
    /// The structured reason this candidate was skipped, when the Stop-hook quality gate
    /// (or a do-not-save instruction) rejected it. <see cref="CaptureSkipReason.None"/> for
    /// ordinary policy skips and for results reconstructed from a persisted finalization.
    /// </summary>
    public CaptureSkipReason SkipReason { get; init; } = CaptureSkipReason.None;

    /// <summary>
    /// A short, capped excerpt of the rejected candidate for the activity record. Never a
    /// full transcript; null when there is no candidate text (e.g. a whole-turn do-not-save).
    /// </summary>
    public string? CandidateExcerpt { get; init; }
}

/// <summary>
/// The structured outcome of finalizing a turn: what AgentRecall captured, suggested,
/// and skipped. This is the definitive answer to "did the turn produce a lesson?", so
/// the agent never has to guess whether the Stop hook captured anything.
/// </summary>
public sealed record TurnFinalizationResult
{
    public IReadOnlyList<FinalizedLesson> Captured { get; init; } = [];
    public IReadOnlyList<FinalizedLesson> Suggested { get; init; } = [];
    public IReadOnlyList<SkippedLesson> Skipped { get; init; } = [];
    public IReadOnlyList<int> Duplicates { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>The persisted <see cref="TurnFinalization"/> id, when stored.</summary>
    public int? Id { get; init; }

    /// <summary>When the finalization was recorded, when known.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Where the finalization was triggered from (e.g. <c>stop_hook</c>), when known.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// The deterministic turn correlation id for this turn, when a prompt was present.
    /// Shared with the retrieval activity recorded at UserPromptSubmit so the turn's
    /// captures and the rules it used join into one summary.
    /// </summary>
    public string? TurnId { get; init; }

    /// <summary>True when this result was returned from a prior identical finalization.</summary>
    public bool FromCache { get; init; }

    /// <summary>
    /// What decided this turn's capture: <c>"SemanticCaptureJudge"</c> when the judge produced a
    /// verdict, <c>"None"</c> when the judge was unavailable, or <c>null</c> for results with no
    /// judged decision (e.g. an empty turn).
    /// </summary>
    public string? DecisionSource { get; init; }

    /// <summary>The judge's decision name (Capture/SuggestCapture/Skip/ReinforceExisting/SupersedeExisting).</summary>
    public string? Decision { get; init; }

    /// <summary>The judge's exact capture reason name, for status reporting.</summary>
    public string? JudgeReason { get; init; }

    /// <summary>The judge's confidence for the decision, when one was made.</summary>
    public double? JudgeConfidence { get; init; }

    /// <summary>The existing rule reinforced or superseded, when applicable.</summary>
    public int? TargetRuleId { get; init; }

    /// <summary>True when nothing of note happened (no captures, suggestions, or skips).</summary>
    public bool IsEmpty =>
        Captured.Count == 0 && Suggested.Count == 0 && Skipped.Count == 0 && Errors.Count == 0;
}
