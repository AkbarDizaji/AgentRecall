using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Abstractions;

/// <summary>The two rules involved in a supersede operation.</summary>
public sealed record SupersedeResult(RecallRule Superseded, RecallRule Replacement);

/// <summary>
/// Drives a rule through its lifecycle (approve, promote, supersede, archive) and
/// reinforces confidence as evidence accumulates.
/// </summary>
public interface IRuleLifecycleService
{
    /// <summary>Pending/Draft → Active.</summary>
    Task<RecallRule> ApproveAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Pending/Active → Promoted.</summary>
    Task<RecallRule> PromoteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Marks the old rule Superseded by the new one and bumps the new rule's version.</summary>
    Task<SupersedeResult> SupersedeAsync(int oldId, int newId, CancellationToken cancellationToken = default);

    /// <summary>Any status → Archived (excluded from search).</summary>
    Task<RecallRule> ArchiveAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Increases a rule's confidence by <paramref name="amount"/> (capped at 1.0)
    /// and auto-promotes it once the promotion threshold is reached.
    /// </summary>
    Task<RecallRule> ReinforceAsync(int id, double amount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently removes a rule from the database. Unlike <see cref="ArchiveAsync"/>,
    /// this is irreversible — there is no status to revert. Deleting a rule that is
    /// currently in force (<see cref="RuleStatus.Active"/> or <see cref="RuleStatus.Promoted"/>)
    /// requires <paramref name="force"/>, since that is more likely a mistake than
    /// deleting a Draft/Pending/Archived/Superseded/Retired rule.
    /// </summary>
    Task<RecallRule> DeleteAsync(int id, bool force = false, CancellationToken cancellationToken = default);
}
