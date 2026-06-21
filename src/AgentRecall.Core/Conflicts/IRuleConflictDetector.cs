using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Conflicts;

/// <summary>
/// Detects conflicts between rules that give competing guidance for the same or
/// overlapping condition. Deterministic and rule-based — no LLM, no embeddings.
/// </summary>
public interface IRuleConflictDetector
{
    /// <summary>
    /// Returns every pairwise conflict among the supplied rules, ascending by the
    /// participating rule ids. The same input always yields the same output.
    /// </summary>
    IReadOnlyList<RuleConflict> Detect(IReadOnlyList<RecallRule> rules);
}
