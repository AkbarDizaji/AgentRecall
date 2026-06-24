namespace AgentRecall.Core.Domain;

/// <summary>
/// A suggested lifecycle action for a rule, produced by analysing rules, events,
/// retrieval, outcomes, and conflicts. Recommendations are advisory: they are never
/// applied automatically unless a human (or an explicit --apply) acts on them.
/// </summary>
public sealed class RuleLifecycleRecommendation
{
    public int Id { get; set; }

    /// <summary>The rule the recommendation is about (for Supersede, the rule to retire).</summary>
    public int RuleId { get; set; }

    /// <summary>For Supersede, the stronger rule that should replace <see cref="RuleId"/>.</summary>
    public int? TargetRuleId { get; set; }

    public RecommendationType RecommendationType { get; set; }

    /// <summary>A one-line rationale.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Deterministic supporting evidence (fixed-order metrics).</summary>
    public string Evidence { get; set; } = string.Empty;

    /// <summary>Confidence in the recommendation, 0.0–1.0.</summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Stable identity (type + rule + target) used to deduplicate and to suppress a
    /// previously rejected recommendation from being proposed again.
    /// </summary>
    public string Signature { get; set; } = string.Empty;

    public RecommendationStatus Status { get; set; } = RecommendationStatus.Suggested;

    /// <summary>Why the recommendation was rejected, when applicable.</summary>
    public string? RejectedReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
