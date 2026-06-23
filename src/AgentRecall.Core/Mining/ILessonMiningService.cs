using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Mining;

/// <summary>
/// Mines historical signals (repeated corrections, PR comments, failures, rejected
/// patterns) and proposes lesson candidates for review. Deterministic and
/// local-only: no LLM, no embeddings. Mining never creates rules on its own —
/// candidates only become rules when a human accepts them.
/// </summary>
public interface ILessonMiningService
{
    /// <summary>Scans history, upserts candidates idempotently, and returns the suggestions.</summary>
    Task<MiningResult> MineAsync(MiningOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts a candidate: creates a <see cref="RecallRule"/> from it and marks the
    /// candidate Accepted. Returns the updated candidate, or null when not found.
    /// </summary>
    Task<LessonCandidate?> AcceptAsync(int candidateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rejects a candidate, recording the reason. Its normalized pattern is then
    /// suppressed from future proposals. Returns the updated candidate, or null.
    /// </summary>
    Task<LessonCandidate?> RejectAsync(int candidateId, string reason, CancellationToken cancellationToken = default);
}
