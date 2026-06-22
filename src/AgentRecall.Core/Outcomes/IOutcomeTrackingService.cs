namespace AgentRecall.Core.Outcomes;

/// <summary>
/// Records real-world outcomes for rules and moves their confidence on evidence.
/// Deterministic and local-only: a fixed delta per outcome type, clamped, with at
/// most one adjustment per outcome event and duplicate suppression.
/// </summary>
public interface IOutcomeTrackingService
{
    /// <summary>Records an outcome and applies the resulting confidence adjustment(s).</summary>
    Task<OutcomeResult> RecordAsync(OutcomeRequest request, CancellationToken cancellationToken = default);
}
