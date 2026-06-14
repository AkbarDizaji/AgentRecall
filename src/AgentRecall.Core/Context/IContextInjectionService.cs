namespace AgentRecall.Core.Context;

/// <summary>
/// Builds the most useful rule context for a task: scores rules on multiple
/// relevance signals (keyword, semantic, domain, task-type, scope), weights by
/// confidence, resolves conflicts through the policy engine, and packs the result
/// into a token budget — with an explanation for every rule.
/// </summary>
public interface IContextInjectionService
{
    Task<ContextInjectionResult> BuildContextAsync(ContextRequest request, CancellationToken cancellationToken = default);
}
