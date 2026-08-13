namespace AgentRecall.Core.Domain;

/// <summary>
/// A turn whose semantic capture judgment AgentRecall asked the session model for, recorded when
/// the Stop hook declined to let the turn finish without one.
///
/// The row is the anchor that joins the block to the judgment that answers it. It carries the
/// turn's own text, so the model resubmits only its verdict — never the transcript — and so the
/// resumed turn is finalized from the same content the block was raised on (identical
/// idempotency hash). It is also the loop guard: <see cref="Attempts"/> bounds how many times one
/// turn may be blocked, without depending on any host-provided "already resumed" signal.
/// </summary>
public sealed class TurnJudgmentRequest
{
    public int Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Deterministic turn correlation id (cwd + prompt) for the turn that was blocked. Empty when
    /// the turn carried no prompt. Not used as the lookup key on resume: a resumed Stop can derive
    /// a different prompt, so <see cref="SessionId"/> and <see cref="Cwd"/> resolve the request.
    /// </summary>
    public string TurnId { get; set; } = string.Empty;

    /// <summary>The host's conversation id, when supplied. The primary resolution key.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>The working directory the turn ran in; the fallback resolution key.</summary>
    public string Cwd { get; set; } = string.Empty;

    /// <summary>Where the blocked turn was finalized from (e.g. <c>stop_hook</c>).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The turn's user prompt, bounded, so the resumed finalization uses the same text.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>The turn's assistant response, bounded, so the resumed finalization matches.</summary>
    public string AssistantResponse { get; set; } = string.Empty;

    /// <summary>Scope granularity derived for the turn, preserved for the resumed finalization.</summary>
    public ScopeLevel ScopeLevel { get; set; } = ScopeLevel.Global;

    /// <summary>Scope identifier (e.g. repository name) derived for the turn.</summary>
    public string ScopeValue { get; set; } = string.Empty;

    /// <summary>How many times AgentRecall has asked for this turn's judgment. The loop guard.</summary>
    public int Attempts { get; set; }

    public JudgmentRequestStatus Status { get; set; } = JudgmentRequestStatus.Outstanding;

    /// <summary>When the request stopped being outstanding (answered or given up on).</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>The judge decision that answered the request, when one did; empty otherwise.</summary>
    public string ResolvedDecision { get; set; } = string.Empty;

    /// <summary>The <see cref="TurnFinalization"/> the answer produced, when one was persisted.</summary>
    public int? FinalizationId { get; set; }
}

/// <summary>The lifecycle of a <see cref="TurnJudgmentRequest"/>.</summary>
public enum JudgmentRequestStatus
{
    /// <summary>AgentRecall asked for a judgment and is still waiting for it.</summary>
    Outstanding = 0,

    /// <summary>The model submitted a verdict and the turn was finalized from it.</summary>
    Resolved = 1,

    /// <summary>
    /// The allowed asks were used up without a verdict. The turn was finalized as unjudged and
    /// AgentRecall stopped asking — the deliberate exit from the block/resume path.
    /// </summary>
    Abandoned = 2,
}
