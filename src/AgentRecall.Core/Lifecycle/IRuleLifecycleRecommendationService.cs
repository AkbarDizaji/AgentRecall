using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Lifecycle;

/// <summary>
/// Analyses rules and their history (events, retrieval, outcomes, conflicts,
/// confidence, status) and proposes lifecycle actions — promote, archive, supersede,
/// review, raise/lower confidence. Deterministic and advisory: suggesting never
/// mutates rules; only <see cref="ApplyAsync"/> does, and only when invoked.
/// </summary>
public interface IRuleLifecycleRecommendationService
{
    /// <summary>
    /// Analyses the corpus and upserts recommendations (idempotent, deduplicated,
    /// rejected ones stay suppressed). Returns the current suggestions. Never mutates rules.
    /// </summary>
    Task<IReadOnlyList<RuleLifecycleRecommendation>> SuggestAsync(RecommendationQuery query, CancellationToken cancellationToken = default);

    /// <summary>Applies a recommendation's action and marks it Applied (Review → Accepted).</summary>
    Task<RuleLifecycleRecommendation?> ApplyAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Rejects a recommendation, suppressing its signature from future proposals.</summary>
    Task<RuleLifecycleRecommendation?> RejectAsync(int id, string reason, CancellationToken cancellationToken = default);
}
