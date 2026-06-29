using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Activity;

/// <summary>
/// Default <see cref="IActivityRecorder"/> over the activity repository. Maps a
/// notice to its persisted shape (ids joined as comma lists, details as newline
/// lines) and stamps it with the configured activity notice level.
/// </summary>
public sealed class ActivityRecorder : IActivityRecorder
{
    private readonly IAgentRecallActivityRepository _repository;
    private readonly AgentRecallOptions _options;

    public ActivityRecorder(IAgentRecallActivityRepository repository, AgentRecallOptions options)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<AgentRecallActivity> RecordAsync(ActivityNotice notice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notice);

        // Deduplicate by operation hash so a repeated/cached operation is logged once.
        if (!string.IsNullOrEmpty(notice.OperationHash))
        {
            var existing = await _repository
                .FindByOperationHashAsync(notice.OperationHash, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }
        }

        var activity = new AgentRecallActivity
        {
            ActivityType = notice.Type,
            Summary = notice.Summary,
            Details = notice.Details.Count == 0 ? null : string.Join('\n', notice.Details),
            RuleIds = JoinIds(notice.RuleIds),
            CandidateIds = JoinIds(notice.CandidateIds),
            RecommendationIds = JoinIds(notice.RecommendationIds),
            Source = notice.Source,
            NoticeLevel = _options.ResolvedActivityNoticeLevel,
            OperationHash = notice.OperationHash,
            TurnId = notice.TurnId,
        };

        return await _repository.AddAsync(activity, cancellationToken).ConfigureAwait(false);
    }

    public Task<AgentRecallActivity?> GetLastAsync(CancellationToken cancellationToken = default) =>
        _repository.GetLatestAsync(cancellationToken);

    public Task<IReadOnlyList<AgentRecallActivity>> ListAsync(int limit, CancellationToken cancellationToken = default) =>
        _repository.ListRecentAsync(limit, cancellationToken);

    private static string? JoinIds(IReadOnlyList<int> ids) =>
        ids.Count == 0 ? null : string.Join(',', ids);
}
