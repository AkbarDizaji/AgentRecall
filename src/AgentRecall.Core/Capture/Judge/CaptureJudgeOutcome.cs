using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Capture.Judge;

/// <summary>What AgentRecall does with a judged turn after validation and threshold mapping.</summary>
public enum JudgePersistAction
{
    /// <summary>Store the rule as an active memory.</summary>
    AutoCapture,

    /// <summary>Store the rule as a pending suggestion.</summary>
    Suggest,

    /// <summary>Store nothing.</summary>
    Skip,

    /// <summary>Record the lesson against an existing rule.</summary>
    Reinforce,

    /// <summary>Store the rule and mark an existing rule superseded by it.</summary>
    Supersede,
}

/// <summary>
/// The deterministic outcome of validating and threshold-mapping a
/// <see cref="CaptureJudgeVerdict"/>: the action to take, the domain values to persist, and a
/// human-readable reason. Produced by <see cref="CaptureJudgeDecisionMapper"/>.
/// </summary>
public sealed record CaptureJudgeOutcome
{
    /// <summary>What to do with the turn.</summary>
    public required JudgePersistAction Action { get; init; }

    /// <summary>The domain category to store the rule under.</summary>
    public RuleCategory Category { get; init; } = RuleCategory.Unknown;

    /// <summary>The nearest domain capture reason for the stored rule.</summary>
    public CaptureReason DomainReason { get; init; } = CaptureReason.None;

    /// <summary>The judge's exact reason name, persisted for status fidelity.</summary>
    public string JudgeReason { get; init; } = string.Empty;

    /// <summary>The judge's decision name, persisted for status fidelity.</summary>
    public string JudgeDecision { get; init; } = string.Empty;

    /// <summary>The status to store an auto-captured/superseding rule under.</summary>
    public RuleStatus Status { get; init; } = RuleStatus.Pending;

    /// <summary>The judge's confidence, carried onto the stored rule.</summary>
    public double Confidence { get; init; }

    /// <summary>The existing rule to reinforce/supersede, when applicable.</summary>
    public int? TargetRuleId { get; init; }

    /// <summary>The normalized rule to store, when the action stores one.</summary>
    public NormalizedRule? Rule { get; init; }

    /// <summary>A short, human-readable account of the decision (why stored/skipped).</summary>
    public string Reason { get; init; } = string.Empty;
}
