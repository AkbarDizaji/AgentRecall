using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Policy;

/// <summary>
/// Decides which of the rules that match a task are effective and which should be
/// ignored, detecting direct conflicts and choosing a winner deterministically.
/// </summary>
public interface IPolicyEngine
{
    /// <summary>
    /// Resolves a pre-selected set of matching rules against a context. Pure and
    /// deterministic: the same inputs always yield the same outcome.
    /// </summary>
    PolicyResolution Resolve(IReadOnlyList<RecallRule> candidates, PolicyContext context);

    /// <summary>
    /// Finds the stored rules relevant to <paramref name="task"/> and resolves
    /// them against <paramref name="context"/>.
    /// </summary>
    Task<PolicyResolution> ResolveForTaskAsync(
        string task,
        PolicyContext context,
        CancellationToken cancellationToken = default);
}
