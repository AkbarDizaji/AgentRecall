using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Conflicts;

/// <summary>The kind of disagreement between two rules.</summary>
public enum RuleConflictType
{
    /// <summary>A conflict was detected but its kind could not be classified.</summary>
    Unknown = 0,

    /// <summary>Opposing actions on the same subject (use vs avoid, unit vs integration).</summary>
    DirectOpposition = 1,

    /// <summary>One rule recommends exactly what the other says to avoid.</summary>
    PreferredVsAvoided = 2,

    /// <summary>The same guidance at different specificity (a broad rule vs a narrow one).</summary>
    BroaderVsSpecific = 3,

    /// <summary>Overlapping rules whose lifecycle status disagrees (e.g. one superseded).</summary>
    StatusConflict = 4,
}

/// <summary>
/// A detected conflict between rules that give competing guidance for the same or
/// overlapping condition.
/// </summary>
public sealed record RuleConflict
{
    /// <summary>Stable identifier derived from the participating rule ids.</summary>
    public required string ConflictId { get; init; }

    /// <summary>The rules in conflict, ascending by id.</summary>
    public required IReadOnlyList<int> RuleIds { get; init; }

    public required RuleConflictType ConflictType { get; init; }

    /// <summary>A one-line description of the disagreement.</summary>
    public required string Summary { get; init; }

    /// <summary>Why the detector considered these rules to conflict.</summary>
    public required string DetectedReason { get; init; }
}

/// <summary>The component scores that decided a single rule's standing in a conflict.</summary>
public sealed record RuleScore
{
    public required int RuleId { get; init; }
    public required double Total { get; init; }
    public required double ScopeSpecificity { get; init; }
    public required double Confidence { get; init; }
    public required double StatusWeight { get; init; }
    public required double Recency { get; init; }
    public required double TriggerSpecificity { get; init; }
}

/// <summary>
/// The deterministic outcome of resolving a conflict: the winning rule, the ones
/// set aside, the score breakdown, and a concise explanation.
/// </summary>
public sealed record RuleResolution
{
    public required int SelectedRuleId { get; init; }
    public required IReadOnlyList<int> IgnoredRuleIds { get; init; }

    /// <summary>Per-rule score breakdown, highest total first.</summary>
    public required IReadOnlyList<RuleScore> ScoreBreakdown { get; init; }

    /// <summary>Short bullet reasons the selected rule won.</summary>
    public required IReadOnlyList<string> Explanation { get; init; }

    /// <summary>How decisive the win was, 0.5–1.0 (1.0 = dominates).</summary>
    public required double Confidence { get; init; }
}

/// <summary>A detected conflict together with its resolution and the rules involved.</summary>
public sealed record ResolvedConflict
{
    public required RuleConflict Conflict { get; init; }
    public required RuleResolution Resolution { get; init; }
    public required RecallRule Selected { get; init; }
    public required IReadOnlyList<RecallRule> Ignored { get; init; }
}
