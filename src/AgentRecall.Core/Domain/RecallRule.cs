using AgentRecall.Core.Capture;

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

    /// <summary>
    /// What kind of knowledge this rule captures. Defaults to
    /// <see cref="RuleCategory.Unknown"/> so rules from earlier versions keep
    /// working unchanged.
    /// </summary>
    public RuleCategory Category { get; set; } = RuleCategory.Unknown;

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

    /// <summary>
    /// Manual ranking weight used by the policy engine to break ties between
    /// matching rules; higher wins. Defaults to 0.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// True when the rule has been deliberately retired from active use. A
    /// deprecated rule is never treated as effective, regardless of status.
    /// </summary>
    public bool Deprecated { get; set; }

    public ScopeLevel ScopeLevel { get; set; } = ScopeLevel.Global;

    /// <summary>The scope identifier (e.g. repo name, language, file path).</summary>
    public string ScopeValue { get; set; } = string.Empty;

    /// <summary>Id of the rule that replaced this one, if any.</summary>
    public int? SupersededById { get; set; }

    /// <summary>
    /// Id of the rule that this rule explicitly supersedes, if any. When both this
    /// rule and its target match a task, the policy engine ignores the target.
    /// </summary>
    public int? SupersedesRuleId { get; set; }

    /// <summary>
    /// The outcome-aware evidence that justified capturing this rule (an observed agent
    /// failure, a user correction, an accepted review, …). Defaults to
    /// <see cref="CaptureReason.None"/> so rules from earlier versions keep working.
    /// </summary>
    public CaptureReason CaptureReason { get; set; } = CaptureReason.None;

    /// <summary>
    /// A short, human-readable account of the evidence behind the capture (e.g. "Agent
    /// flattened nested template conditionals and changed else-branch semantics; user
    /// corrected it."). Empty when there was no outcome evidence.
    /// </summary>
    public string EvidenceSummary { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the rule was last applied to a task, if ever.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}
