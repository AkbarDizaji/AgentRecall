namespace AgentRecall.Core.Domain;

/// <summary>
/// A learned rule: a piece of guidance derived from an observed mistake, scoped
/// to where it applies and versioned so it can be superseded over time.
/// </summary>
public sealed class RecallRule
{
    public int Id { get; set; }

    /// <summary>Monotonic version of this rule's content; starts at 1.</summary>
    public int Version { get; set; } = 1;

    public RuleStatus Status { get; set; } = RuleStatus.Active;

    /// <summary>What situation triggers this rule (the cue to recall it).</summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>The mistake this rule exists to prevent.</summary>
    public string Mistake { get; set; } = string.Empty;

    /// <summary>The actionable guidance.</summary>
    public string RuleText { get; set; } = string.Empty;

    /// <summary>Relevant technical context (frameworks, versions, constraints).</summary>
    public string TechnicalContext { get; set; } = string.Empty;

    /// <summary>Free-form tags. Stored as a comma-separated string.</summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>Confidence in the rule, 0.0–1.0.</summary>
    public double Confidence { get; set; }

    public ScopeLevel ScopeLevel { get; set; } = ScopeLevel.Global;

    /// <summary>The scope identifier (e.g. repo name, language, file path).</summary>
    public string ScopeValue { get; set; } = string.Empty;

    /// <summary>Id of the rule that replaced this one, if any.</summary>
    public int? SupersededById { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
