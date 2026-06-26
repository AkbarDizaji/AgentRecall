using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Activity;

/// <summary>
/// Persists user-facing activity notices and reads them back. Recording is
/// deduplicated by <see cref="ActivityNotice.OperationHash"/>, so a repeated (e.g.
/// cached) operation never writes a duplicate record.
/// </summary>
public interface IActivityRecorder
{
    /// <summary>
    /// Persists a notice and returns the stored record. When the notice carries an
    /// <see cref="ActivityNotice.OperationHash"/> that already exists, no new record
    /// is written and the existing one is returned.
    /// </summary>
    Task<AgentRecallActivity> RecordAsync(ActivityNotice notice, CancellationToken cancellationToken = default);

    /// <summary>The most recently recorded activity, or null when none exist.</summary>
    Task<AgentRecallActivity?> GetLastAsync(CancellationToken cancellationToken = default);

    /// <summary>The most recent activities, newest first, capped at <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<AgentRecallActivity>> ListAsync(int limit, CancellationToken cancellationToken = default);
}
