using AgentRecall.Core.Domain;

namespace AgentRecall.Core.CareerImpact;

/// <summary>
/// The opt-in career-impact coordinator: runs the deterministic detector at the end of a turn
/// and persists a candidate only when the configured mode surfaces one, and reads back the
/// last candidate for the on-demand <c>career</c> commands. It never calls an LLM, uses
/// embeddings, or touches the network.
/// </summary>
public interface ICareerImpactService
{
    /// <summary>
    /// Runs the detector for one finalized turn. Returns the persisted candidate when the
    /// <c>career-impact</c> pack is installed, the mode is not <c>Silent</c>, and the result
    /// is surfaced (significant work — or any signal under <c>Always</c>); otherwise null.
    /// Idempotent per turn content, so a repeated Stop hook never double-records.
    /// </summary>
    Task<CareerImpactCandidate?> AnalyzeTurnAsync(CareerImpactTurnRequest request, CancellationToken cancellationToken = default);

    /// <summary>The most recently detected candidate, or null when none exist.</summary>
    Task<CareerImpactCandidate?> GetLastAsync(CancellationToken cancellationToken = default);

    /// <summary>The most recent candidate for a turn, or null when none matches.</summary>
    Task<CareerImpactCandidate?> GetForTurnAsync(string turnId, CancellationToken cancellationToken = default);

    /// <summary>True when the <c>career-impact</c> seed pack has in-force (non-archived) rules.</summary>
    Task<bool> IsPackInstalledAsync(CancellationToken cancellationToken = default);
}
