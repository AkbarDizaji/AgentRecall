namespace AgentRecall.Core.Domain;

/// <summary>
/// A single observed outcome for a rule: what happened, how much it moved the
/// rule's confidence, and why. The ledger of these rows explains a rule's
/// evidence-based confidence over time.
/// </summary>
public sealed class RuleOutcome
{
    public int Id { get; set; }

    /// <summary>The rule whose confidence this outcome adjusted.</summary>
    public int RuleId { get; set; }

    /// <summary>The retrieval this outcome was attributed to, if any.</summary>
    public string? RetrievalId { get; set; }

    /// <summary>The task or interaction this outcome belongs to, if any.</summary>
    public string? TaskId { get; set; }

    public OutcomeType Type { get; set; }

    /// <summary>The confidence change actually applied (after clamping).</summary>
    public double ConfidenceDelta { get; set; }

    /// <summary>Why the outcome was recorded, for explainability.</summary>
    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
