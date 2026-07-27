using AgentRecall.Core.Domain;

namespace AgentRecall.Core.DocOpportunity;

/// <summary>
/// The opt-in document-opportunity coordinator: runs the host-supplied judge at the end of a
/// turn and persists a candidate only when it offers a document, and reads back the last
/// candidate for the on-demand <c>document</c> commands. It never calls an LLM, uses embeddings,
/// or touches the network, and it never writes a file by itself.
/// </summary>
public interface IDocOpportunityService
{
    /// <summary>
    /// Runs the judge for one finalized turn. Returns the persisted candidate when the mode is
    /// not <c>Off</c>, the judge offered a document, and the verdict is structurally valid;
    /// otherwise null. Idempotent per turn content, so a repeated Stop hook never double-records.
    /// </summary>
    Task<DocOpportunityCandidate?> AnalyzeTurnAsync(DocOpportunityTurnRequest request, CancellationToken cancellationToken = default);

    /// <summary>The most recently offered candidate, or null when none exist.</summary>
    Task<DocOpportunityCandidate?> GetLastAsync(CancellationToken cancellationToken = default);

    /// <summary>The most recent candidate for a turn, or null when none matches.</summary>
    Task<DocOpportunityCandidate?> GetForTurnAsync(string turnId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a candidate as written to <paramref name="path"/> after <c>document write</c>
    /// succeeds, or null when no candidate with that id exists.
    /// </summary>
    Task<DocOpportunityCandidate?> MarkWrittenAsync(int candidateId, string path, CancellationToken cancellationToken = default);
}
