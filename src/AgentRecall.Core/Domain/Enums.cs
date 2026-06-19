namespace AgentRecall.Core.Domain;

/// <summary>Lifecycle state of a <see cref="RecallRule"/>.</summary>
public enum RuleStatus
{
    Draft = 0,
    Active = 1,
    Superseded = 2,
    Retired = 3,

    /// <summary>Extracted from feedback but not yet reviewed/promoted.</summary>
    Pending = 4,

    /// <summary>Reviewed and accepted as a high-quality, applicable rule.</summary>
    Promoted = 5,

    /// <summary>Retired/rejected and kept only for history; excluded from search.</summary>
    Archived = 6,
}

/// <summary>
/// The granularity at which a rule or scope applies, from broadest to narrowest.
/// </summary>
public enum ScopeLevel
{
    Global = 0,
    Language = 1,
    Repository = 2,
    Directory = 3,
    File = 4,
}

/// <summary>The kind of activity recorded by a <see cref="RecallEvent"/>.</summary>
public enum RecallEventType
{
    RuleCreated = 0,
    RuleUpdated = 1,
    RuleApplied = 2,
    RuleSuperseded = 3,
    MistakeObserved = 4,

    /// <summary>Several rules were merged into a single canonical rule.</summary>
    RulesCompressed = 5,

    /// <summary>A rule was promoted to high-trust status.</summary>
    RulePromoted = 6,

    /// <summary>A rule was archived (retired from search).</summary>
    RuleArchived = 7,

    /// <summary>A captured candidate was rejected as not memory-worthy.</summary>
    RuleRejected = 8,
}
