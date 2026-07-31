namespace AgentRecall.Core.Abstractions;

/// <summary>The rules a bulk approve/reject swept, and the chat session they were scoped to.</summary>
public sealed record PendingCaptureApprovalBatch
{
    public IReadOnlyList<int> RuleIds { get; init; } = [];

    /// <summary>The session id the batch resolved to, or null when nothing was pending.</summary>
    public string? SessionId { get; init; }
}

/// <summary>
/// Resolves rules the Stop-hook capture-approval gate parked Pending, per the user's yes/no (or
/// "yes to all") reply in chat. Every rule the gate parks is stamped with the host's session id
/// (see <see cref="Domain.RecallRule.SessionId"/>), so "all" can be scoped to one conversation
/// without the caller having to track or pass that id explicitly.
/// </summary>
public interface IPendingCaptureApprovalService
{
    /// <summary>Approves one pending rule (Pending/Draft → Active).</summary>
    Task<Domain.RecallRule> ApproveAsync(int ruleId, CancellationToken cancellationToken = default);

    /// <summary>Rejects one pending rule (→ Archived).</summary>
    Task<Domain.RecallRule> RejectAsync(int ruleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves every rule still awaiting approval in one chat. When <paramref name="sessionId"/>
    /// is omitted, resolves to the most recently captured session with rules still pending —
    /// the practical case, since the model doesn't otherwise know the host's session id.
    /// </summary>
    Task<PendingCaptureApprovalBatch> ApproveAllAsync(string? sessionId = null, CancellationToken cancellationToken = default);

    /// <summary>Rejects every rule still awaiting approval in one chat. See <see cref="ApproveAllAsync"/>.</summary>
    Task<PendingCaptureApprovalBatch> RejectAllAsync(string? sessionId = null, CancellationToken cancellationToken = default);
}
