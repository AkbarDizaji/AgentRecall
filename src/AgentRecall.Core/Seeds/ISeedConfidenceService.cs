namespace AgentRecall.Core.Seeds;

/// <summary>Deterministic passive confidence reinforcement for seed rules.</summary>
public interface ISeedConfidenceService
{
    /// <summary>
    /// Credits every seed rule for its uneventful retrievals since the last run, raising
    /// confidence by a small capped amount. Idempotent: re-running credits only genuinely
    /// new uses. Returns the rules whose confidence actually moved.
    /// </summary>
    Task<SeedReinforcementResult> ReinforceAsync(CancellationToken cancellationToken = default);
}

/// <summary>One seed rule's confidence movement from passive reinforcement.</summary>
public sealed record SeedConfidenceAdjustment
{
    public required int RuleId { get; init; }
    public required string Title { get; init; }
    public required double PreviousConfidence { get; init; }
    public required double NewConfidence { get; init; }
    public required int UneventfulUses { get; init; }
}

/// <summary>The result of a passive reinforcement pass.</summary>
public sealed record SeedReinforcementResult
{
    public required IReadOnlyList<SeedConfidenceAdjustment> Adjustments { get; init; }
}
