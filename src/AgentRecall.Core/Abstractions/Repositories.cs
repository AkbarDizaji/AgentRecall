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
