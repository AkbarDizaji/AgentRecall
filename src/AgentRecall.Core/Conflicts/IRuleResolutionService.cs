using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Conflicts;

/// <summary>
/// Selects the winning rule among a set of conflicting rules using a deterministic
/// weighted score and explains the choice. Pure — it never mutates the rules.
/// </summary>
public interface IRuleResolutionService
{
    /// <summary>
    /// Resolves a conflict between two or more rules. The same input always yields
    /// the same selection, score breakdown, and explanation.
    /// </summary>
    RuleResolution Resolve(IReadOnlyList<RecallRule> conflictingRules);
}
