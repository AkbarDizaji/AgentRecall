using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Services;

/// <summary>Default <see cref="IPendingCaptureApprovalService"/>.</summary>
public sealed class PendingCaptureApprovalService : IPendingCaptureApprovalService
{
    private readonly IRecallRuleRepository _rules;
    private readonly IRuleLifecycleService _lifecycle;

    public PendingCaptureApprovalService(IRecallRuleRepository rules, IRuleLifecycleService lifecycle)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public Task<RecallRule> ApproveAsync(int ruleId, CancellationToken cancellationToken = default) =>
        _lifecycle.ApproveAsync(ruleId, cancellationToken);

    public Task<RecallRule> RejectAsync(int ruleId, CancellationToken cancellationToken = default) =>
        _lifecycle.ArchiveAsync(ruleId, cancellationToken);

    public Task<PendingCaptureApprovalBatch> ApproveAllAsync(string? sessionId = null, CancellationToken cancellationToken = default) =>
        ResolveAllAsync(sessionId, approve: true, cancellationToken);

    public Task<PendingCaptureApprovalBatch> RejectAllAsync(string? sessionId = null, CancellationToken cancellationToken = default) =>
        ResolveAllAsync(sessionId, approve: false, cancellationToken);

    private async Task<PendingCaptureApprovalBatch> ResolveAllAsync(string? sessionId, bool approve, CancellationToken cancellationToken)
    {
        var pending = (await _rules.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(r => r.Status == RuleStatus.Pending && !string.IsNullOrEmpty(r.SessionId))
            .ToList();

        // No session named: resolve to whichever chat most recently parked a rule pending
        // approval — the model doesn't otherwise know the host's session id, and in the
        // common case there is only one conversation with anything outstanding at a time.
        var resolvedSessionId = sessionId ?? pending
            .OrderByDescending(r => r.Id)
            .Select(r => r.SessionId)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(resolvedSessionId))
        {
            return new PendingCaptureApprovalBatch { RuleIds = [], SessionId = null };
        }

        var targets = pending
            .Where(r => string.Equals(r.SessionId, resolvedSessionId, StringComparison.Ordinal))
            .Select(r => r.Id)
            .ToList();

        foreach (var id in targets)
        {
            if (approve)
            {
                await _lifecycle.ApproveAsync(id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _lifecycle.ArchiveAsync(id, cancellationToken).ConfigureAwait(false);
            }
        }

        return new PendingCaptureApprovalBatch { RuleIds = targets, SessionId = resolvedSessionId };
    }
}
