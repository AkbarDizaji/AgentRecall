using AgentRecall.Core.Capture.Judge;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Finalization;

/// <summary>
/// The seam that lets AgentRecall insist on a semantic capture judgment before a turn finishes.
///
/// AgentRecall cannot produce a verdict itself — it makes no model or network calls — so the only
/// way to stop a turn from ending unjudged is to decline to finish it and ask the session model,
/// which is the judge, to submit one. This gate owns both halves of that exchange: deciding
/// whether to ask (and recording the ask), and finalizing the turn from the verdict that answers
/// it. It never decides what to remember.
/// </summary>
public interface ITurnJudgmentGate
{
    /// <summary>
    /// Decides what the Stop-hook path should do with a turn. When the decision is
    /// <see cref="JudgmentEnforcementAction.RequestJudgment"/> the ask is persisted (so the turn's
    /// text survives the resume and the attempt is counted); when it is
    /// <see cref="JudgmentEnforcementAction.ProceedUnjudged"/> any outstanding ask for the turn is
    /// closed as abandoned.
    /// </summary>
    Task<JudgmentGateDecision> EvaluateAsync(TurnFinalizationInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalizes a turn from the verdict the model submitted, resolving the outstanding request it
    /// answers. A rejection (<see cref="JudgeDecision.Skip"/>) is a valid verdict and closes the
    /// request just as a capture does.
    /// </summary>
    Task<JudgmentSubmissionResult> SubmitAsync(JudgmentSubmission submission, CancellationToken cancellationToken = default);

    /// <summary>The request currently awaiting a verdict for a chat/directory, or null.</summary>
    Task<TurnJudgmentRequest?> FindOutstandingAsync(
        string? sessionId, string? cwd, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the ask a judged turn answers, whichever route supplied the verdict — the tool, a
    /// judgment on the payload, or a hand-piped <c>finalize-turn</c>. Without this, a verdict that
    /// arrived by a route other than the tool would leave the request outstanding and the status
    /// surfaces would keep reporting a turn as unanswered after it was answered. A no-op unless the
    /// finalization was actually judged.
    /// </summary>
    Task CloseOutstandingAsync(
        TurnFinalizationInput input, TurnFinalizationResult result, CancellationToken cancellationToken = default);
}

/// <summary>The gate's decision for one turn, plus the state it recorded.</summary>
public sealed record JudgmentGateDecision
{
    /// <summary>What the caller should do.</summary>
    public required JudgmentEnforcementAction Action { get; init; }

    /// <summary>Why, in words fit for the recorded diagnostics.</summary>
    public required string Reason { get; init; }

    /// <summary>The persisted request this ask belongs to, when one was recorded.</summary>
    public int? RequestId { get; init; }

    /// <summary>How many times this turn has now been asked for a judgment.</summary>
    public int Attempts { get; init; }

    /// <summary>
    /// The instruction handed back to the model when the turn is blocked — the only channel
    /// AgentRecall has for saying what it needs. Null for every non-blocking decision.
    /// </summary>
    public string? BlockReason { get; init; }
}

/// <summary>A verdict the model submitted for a turn, with the hints used to find that turn.</summary>
public sealed record JudgmentSubmission
{
    /// <summary>The verdict itself — the same shape the Stop-hook payload carries.</summary>
    public required CaptureJudgeVerdict Verdict { get; init; }

    /// <summary>The request being answered, when the model quotes the id back.</summary>
    public int? RequestId { get; init; }

    /// <summary>The host conversation id, the primary way an outstanding request is found.</summary>
    public string? SessionId { get; init; }

    /// <summary>The working directory, the fallback way an outstanding request is found.</summary>
    public string? Cwd { get; init; }

    /// <summary>The turn correlation id, when the caller knows it.</summary>
    public string? TurnId { get; init; }

    /// <summary>
    /// The turn's prompt, used only when no request is outstanding — a verdict submitted
    /// unprompted still finalizes a turn, it just has to say which turn.
    /// </summary>
    public string? Prompt { get; init; }

    /// <summary>The turn's assistant response, used only when no request is outstanding.</summary>
    public string? AssistantResponse { get; init; }

    /// <summary>
    /// Scope granularity for a verdict submitted with no outstanding request. Derived by the caller
    /// (the CLI knows how to read a repository from a path); the gate never touches the filesystem.
    /// </summary>
    public ScopeLevel ScopeLevel { get; init; } = ScopeLevel.Global;

    /// <summary>Scope identifier (e.g. repository name) for an unprompted submission.</summary>
    public string? ScopeValue { get; init; }

    /// <summary>Where the submission came from, for the recorded source.</summary>
    public string Source { get; init; } = "submit_capture_judgment";
}

/// <summary>The outcome of submitting a verdict.</summary>
public sealed record JudgmentSubmissionResult
{
    /// <summary>True when the verdict was accepted and the turn finalized.</summary>
    public required bool Submitted { get; init; }

    /// <summary>Why the verdict was not accepted, when it wasn't.</summary>
    public string? Reason { get; init; }

    /// <summary>The request resolved by this verdict, when it answered one.</summary>
    public int? RequestId { get; init; }

    /// <summary>True when no request was outstanding and the turn came from the submission itself.</summary>
    public bool WasUnprompted { get; init; }

    /// <summary>The finalization the verdict produced, when it was accepted.</summary>
    public TurnFinalizationResult? Finalization { get; init; }
}
