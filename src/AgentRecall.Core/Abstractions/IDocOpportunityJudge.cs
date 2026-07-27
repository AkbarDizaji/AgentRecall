using AgentRecall.Core.Capture.Judge;

namespace AgentRecall.Core.Abstractions;

/// <summary>
/// The seam through which AgentRecall obtains the document-opportunity judge's verdict for a
/// completed turn. The judge — the model, not AgentRecall — decides whether the turn is a good
/// moment to offer generating a document (an incident report, RFC, proposal, ADR, postmortem,
/// or runbook); the system only validates the returned <see cref="DocOpportunityVerdict"/> and
/// persists it safely. Offering a document never writes a file by itself — a file is only
/// written later, when the user explicitly agrees and the host runs
/// <c>agentrecall document write</c>.
///
/// A <c>null</c> return means the judge is unavailable for this turn (no verdict was supplied,
/// or no provider is configured). The caller treats that as "nothing offered" — it never falls
/// back to keyword-driven detection.
/// </summary>
public interface IDocOpportunityJudge
{
    /// <summary>Returns the judge's verdict for the turn, or <c>null</c> when unavailable.</summary>
    Task<DocOpportunityVerdict?> JudgeAsync(DocOpportunityJudgeInput input, CancellationToken cancellationToken = default);
}
