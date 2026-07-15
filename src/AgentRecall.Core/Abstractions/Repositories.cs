using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Abstractions;

/// <summary>
/// A database-side filter for rules, so callers don't load the whole table and filter
/// in memory. Every set field is applied as a WHERE clause at the DB layer.
/// </summary>
public sealed record RuleQuery
{
    /// <summary>Match only this scope level when set.</summary>
    public ScopeLevel? ScopeLevel { get; init; }

    /// <summary>
    /// Match this scope value (case-insensitive) when non-null. An empty string matches
    /// the global/empty scope; <c>null</c> means "any scope value".
    /// </summary>
    public string? ScopeValue { get; init; }

    /// <summary>Include only rules whose status is in this set, when provided.</summary>
    public IReadOnlyCollection<RuleStatus>? Statuses { get; init; }

    /// <summary>Exclude rules whose status is in this set, when provided.</summary>
    public IReadOnlyCollection<RuleStatus>? ExcludeStatuses { get; init; }

    /// <summary>Match only rules with this deprecated flag when set.</summary>
    public bool? Deprecated { get; init; }
}

/// <summary>Persistence for <see cref="RecallRule"/>.</summary>
public interface IRecallRuleRepository : IRepository<RecallRule>
{
    /// <summary>
    /// Returns rules matching <paramref name="query"/>, filtered at the database layer
    /// (scope, status, deprecation) rather than by loading the full table into memory.
    /// </summary>
    Task<IReadOnlyList<RecallRule>> QueryAsync(RuleQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Persistence for <see cref="RecallEvent"/>.</summary>
public interface IRecallEventRepository : IRepository<RecallEvent>
{
}

/// <summary>Persistence for <see cref="RecallScope"/>.</summary>
public interface IRecallScopeRepository : IRepository<RecallScope>
{
}

/// <summary>Persistence for <see cref="RetrievalRecord"/>.</summary>
public interface IRetrievalRecordRepository : IRepository<RetrievalRecord>
{
}

/// <summary>Persistence for <see cref="RuleOutcome"/>.</summary>
public interface IRuleOutcomeRepository : IRepository<RuleOutcome>
{
}

/// <summary>Persistence for <see cref="LessonCandidate"/>.</summary>
public interface ILessonCandidateRepository : IRepository<LessonCandidate>
{
}

/// <summary>Persistence for <see cref="RuleLifecycleRecommendation"/>.</summary>
public interface IRuleLifecycleRecommendationRepository : IRepository<RuleLifecycleRecommendation>
{
}

/// <summary>Persistence for <see cref="TurnFinalization"/>.</summary>
public interface ITurnFinalizationRepository : IRepository<TurnFinalization>
{
}

/// <summary>Persistence for <see cref="CareerImpactCandidate"/>.</summary>
public interface ICareerImpactCandidateRepository : IRepository<CareerImpactCandidate>
{
    /// <summary>The most recently detected candidate, or null when none exist.</summary>
    Task<CareerImpactCandidate?> GetLatestAsync(CancellationToken cancellationToken = default);

    /// <summary>The candidate with the given operation hash, or null when none matches.</summary>
    Task<CareerImpactCandidate?> FindByOperationHashAsync(string operationHash, CancellationToken cancellationToken = default);

    /// <summary>The most recent candidate for a turn, or null when none matches.</summary>
    Task<CareerImpactCandidate?> FindByTurnAsync(string turnId, CancellationToken cancellationToken = default);
}

/// <summary>Persistence for <see cref="AgentRecallActivity"/>.</summary>
public interface IAgentRecallActivityRepository : IRepository<AgentRecallActivity>
{
    /// <summary>The most recently recorded activity, or null when none exist.</summary>
    Task<AgentRecallActivity?> GetLatestAsync(CancellationToken cancellationToken = default);

    /// <summary>The most recent activities, newest first, capped at <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<AgentRecallActivity>> ListRecentAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>All activities recorded for a given turn correlation id, newest first.</summary>
    Task<IReadOnlyList<AgentRecallActivity>> ListByTurnAsync(string turnId, CancellationToken cancellationToken = default);

    /// <summary>The activity with the given operation hash, or null when none matches.</summary>
    Task<AgentRecallActivity?> FindByOperationHashAsync(string operationHash, CancellationToken cancellationToken = default);
}
