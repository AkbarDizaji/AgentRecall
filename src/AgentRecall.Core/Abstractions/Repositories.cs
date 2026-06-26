using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Abstractions;

/// <summary>Persistence for <see cref="RecallRule"/>.</summary>
public interface IRecallRuleRepository : IRepository<RecallRule>
{
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

/// <summary>Persistence for <see cref="AgentRecallActivity"/>.</summary>
public interface IAgentRecallActivityRepository : IRepository<AgentRecallActivity>
{
    /// <summary>The most recently recorded activity, or null when none exist.</summary>
    Task<AgentRecallActivity?> GetLatestAsync(CancellationToken cancellationToken = default);

    /// <summary>The most recent activities, newest first, capped at <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<AgentRecallActivity>> ListRecentAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>The activity with the given operation hash, or null when none matches.</summary>
    Task<AgentRecallActivity?> FindByOperationHashAsync(string operationHash, CancellationToken cancellationToken = default);
}
