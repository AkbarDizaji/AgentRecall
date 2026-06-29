namespace AgentRecall.Core.Summary;

/// <summary>
/// Aggregates a single turn's recorded memory activity into one <see cref="TurnSummary"/>.
/// It only reads existing structured activity and finalization records; it never changes
/// capture decisions, retrieval ranking, or rule lifecycle.
/// </summary>
public interface ITurnSummaryService
{
    /// <summary>
    /// Builds the summary for the most recent turn. Resolves the turn from the latest
    /// activity that carries a turn id; when none does, it falls back to a conservative
    /// time window anchored on the latest activity. Returns an empty summary when there is
    /// no activity at all.
    /// </summary>
    Task<TurnSummary> BuildLastAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the summary for a specific turn id. A null or empty id delegates to
    /// <see cref="BuildLastAsync"/>.
    /// </summary>
    Task<TurnSummary> BuildForTurnAsync(string? turnId, CancellationToken cancellationToken = default);
}
