using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Context;

/// <summary>The kind of work a task represents, used to bias rule relevance.</summary>
public enum TaskType
{
    Unknown,
    Feature,
    BugFix,
    Refactor,
    Review,
    Test,
    Security,
    Performance,
    Documentation,
}

/// <summary>How strongly a retrieved rule should influence the task.</summary>
public enum RuleImportance
{
    /// <summary>Authoritative guidance the agent must apply.</summary>
    MustFollow,

    /// <summary>Relevant guidance worth considering.</summary>
    Suggested,

    /// <summary>An anti-pattern or past mistake to actively avoid.</summary>
    Warning,
}

/// <summary>Everything known about the task that context is being built for.</summary>
public sealed record ContextRequest
{
    /// <summary>What the agent is about to do, in plain language.</summary>
    public required string Task { get; init; }

    public TaskType TaskType { get; init; } = TaskType.Unknown;

    /// <summary>The scope granularity of the work (e.g. Repository), if known.</summary>
    public ScopeLevel? ScopeLevel { get; init; }

    /// <summary>The project/scope identifier (e.g. repo name), if known.</summary>
    public string? ScopeValue { get; init; }

    /// <summary>Files the task touches (used for domain matching).</summary>
    public IReadOnlyList<string> FileNames { get; init; } = [];

    /// <summary>Code entities the task changes — types, methods, concepts.</summary>
    public IReadOnlyList<string> ChangedEntities { get; init; } = [];

    /// <summary>Approximate token budget for the injected context.</summary>
    public int TokenBudget { get; init; } = 1500;

    /// <summary>Maximum number of rules to return across all buckets.</summary>
    public int Limit { get; init; } = 25;

    /// <summary>
    /// Rule ids to exclude from this retrieval entirely — neither injected nor recorded as
    /// used. The PreToolUse hook sets this to the rules already surfaced earlier in the same
    /// turn, so a rule is not re-injected on every file write (context bloat) or re-counted as
    /// used on every write (usage-telemetry inflation).
    /// </summary>
    public IReadOnlySet<int> ExcludeRuleIds { get; init; } = EmptyIds;

    private static readonly IReadOnlySet<int> EmptyIds = new HashSet<int>();

    /// <summary>
    /// When true, also consider Pending rules (never as must-follow). Off by
    /// default: only Active and Promoted rules are returned.
    /// </summary>
    public bool IncludePending { get; init; }

    /// <summary>
    /// Max Pending rules to keep when <see cref="IncludePending"/> is true, keeping
    /// only the freshest/highest-scoring ones. Null (default) leaves every
    /// relevance-qualifying Pending rule in place — the hook sets this so an
    /// unreviewed suggestion can resurface for reinforcement without flooding the
    /// context; other callers stay uncapped.
    /// </summary>
    public int? PendingCap { get; init; }

    /// <summary>
    /// When true, records that the selected rules were retrieved: a RuleApplied
    /// event per rule plus a LastUsedAt bump, so learning reports can measure
    /// which rules are actually helping. Off by default so pure ranking (and its
    /// tests) stays side-effect free; the real retrieval entry points opt in.
    /// </summary>
    public bool RecordUsage { get; init; }
}

/// <summary>A rule selected for injection, with its score and an explanation.</summary>
public sealed record InjectedRule
{
    public required RecallRule Rule { get; init; }
    public required RuleImportance Importance { get; init; }

    /// <summary>Final usefulness score (relevance weighted by confidence/status).</summary>
    public required double Score { get; init; }

    /// <summary>Raw 0–1 relevance before confidence weighting.</summary>
    public required double Relevance { get; init; }

    /// <summary>Why this rule was retrieved, in plain language.</summary>
    public required string Explanation { get; init; }

    /// <summary>The individual signals that matched.</summary>
    public required IReadOnlyList<string> MatchReasons { get; init; }

    /// <summary>Estimated tokens this rule contributes to the context.</summary>
    public required int EstimatedTokens { get; init; }
}

/// <summary>The context assembled for a task: rules bucketed by importance.</summary>
public sealed record ContextInjectionResult
{
    public required IReadOnlyList<InjectedRule> MustFollow { get; init; }
    public required IReadOnlyList<InjectedRule> Suggested { get; init; }
    public required IReadOnlyList<InjectedRule> Warnings { get; init; }

    public required int TokensUsed { get; init; }
    public required int TokenBudget { get; init; }
    public required string Explanation { get; init; }

    /// <summary>
    /// Conflicts whose resolution changed what was injected: the selected rule is
    /// present and the rules it beat were suppressed. Empty when nothing conflicts.
    /// </summary>
    public IReadOnlyList<Conflicts.ResolvedConflict> Conflicts { get; init; } = [];

    /// <summary>
    /// The id of the retrieval record written when usage was recorded, so outcomes
    /// can later be attached to the rules that were injected. Null when usage was
    /// not recorded or nothing was injected.
    /// </summary>
    public string? RetrievalId { get; init; }

    /// <summary>All injected rules, highest priority first.</summary>
    public IEnumerable<InjectedRule> All => MustFollow.Concat(Warnings).Concat(Suggested);
}
