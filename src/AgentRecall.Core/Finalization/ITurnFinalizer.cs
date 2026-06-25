namespace AgentRecall.Core.Finalization;

/// <summary>
/// The single, deterministic owner of capture for a completed turn. It extracts
/// candidate lessons, classifies their worthiness, detects duplicates and conflicts,
/// and decides — through the same <see cref="Abstractions.IFeedbackService"/> path
/// every other capture flow uses — whether to auto-capture, suggest, or skip each one.
/// The agent does not guess and is not asked; AgentRecall decides and records the
/// result so it can be queried later.
/// </summary>
public interface ITurnFinalizer
{
    /// <summary>
    /// Finalizes a completed turn: extracts and routes candidate lessons, persists the
    /// outcome, and returns a structured summary. Never throws for a turn-content
    /// reason; non-fatal problems are reported in <see cref="TurnFinalizationResult.Errors"/>.
    /// </summary>
    Task<TurnFinalizationResult> FinalizeAsync(
        TurnFinalizationInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the most recent finalization result, optionally for a specific working
    /// directory, or <c>null</c> when no turn has been finalized yet.
    /// </summary>
    Task<TurnFinalizationResult?> GetLastAsync(
        string? cwd = null,
        CancellationToken cancellationToken = default);
}
