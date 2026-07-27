using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture.Judge;

namespace AgentRecall.Core.Services;

/// <summary>
/// The default <see cref="IDocOpportunityJudge"/>. AgentRecall itself calls no model or
/// network; the judgement is produced by the host (the session model) and supplied on the
/// turn payload. This judge simply returns the verdict the host already produced — or
/// <c>null</c> when none was supplied, which callers treat as "judge unavailable → skip".
///
/// A future live provider would implement <see cref="IDocOpportunityJudge"/> to build its own
/// verdict from <see cref="DocOpportunityJudgeInput"/> and would ignore
/// <see cref="DocOpportunityJudgeInput.SuppliedVerdict"/>.
/// </summary>
public sealed class HostSuppliedDocOpportunityJudge : IDocOpportunityJudge
{
    public Task<DocOpportunityVerdict?> JudgeAsync(DocOpportunityJudgeInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Task.FromResult(input.SuppliedVerdict);
    }
}
