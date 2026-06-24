using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Dna;

/// <summary>
/// A distilled "engineering personality" of a repository: the conventions,
/// patterns, risks, and recurring lessons a new developer or AI coding agent
/// needs to understand how the project works. Computed from the local rule
/// corpus, the event ledger, mined lessons, outcomes, and accepted lifecycle
/// recommendations — no external services and no LLM calls. Deterministic for a
/// given dataset and <see cref="ProjectDnaOptions.AsOf"/>.
/// </summary>
public sealed record ProjectDnaReport
{
    /// <summary>The reference instant the report was generated for (stable, injected).</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>The scope the report was filtered to, if any.</summary>
    public required DnaScope Scope { get; init; }

    /// <summary>The ordered DNA sections (always the full set, possibly empty).</summary>
    public required IReadOnlyList<DnaSection> Sections { get; init; }

    /// <summary>How many of each source signal fed the report.</summary>
    public required DnaSourceCounts SourceCounts { get; init; }
}

/// <summary>The scope a DNA report was filtered to. Null level means "all scopes".</summary>
public sealed record DnaScope
{
    public ScopeLevel? Level { get; init; }
    public string? Value { get; init; }
}

/// <summary>A single DNA section: a stable key, a human title, and its items.</summary>
public sealed record DnaSection
{
    /// <summary>Stable machine-readable key (e.g. "core-principles").</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable heading (e.g. "Core Principles").</summary>
    public required string Title { get; init; }

    /// <summary>The ranked items in this section, highest value first.</summary>
    public required IReadOnlyList<DnaItem> Items { get; init; }
}

/// <summary>A single line of distilled guidance within a section.</summary>
public sealed record DnaItem
{
    /// <summary>The onboarding-ready guidance text.</summary>
    public required string Text { get; init; }

    /// <summary>The rule id(s) this item was derived from (empty for mined-only items).</summary>
    public required IReadOnlyList<int> RuleIds { get; init; }

    /// <summary>Confidence of the backing rule/candidate, 0.0–1.0 (null when not applicable).</summary>
    public double? Confidence { get; init; }

    /// <summary>The knowledge category this item represents.</summary>
    public string? Category { get; init; }

    /// <summary>Fixed-order, human-readable evidence for why this item ranks here.</summary>
    public required IReadOnlyList<string> Evidence { get; init; }
}

/// <summary>How many of each source signal contributed to a DNA report.</summary>
public sealed record DnaSourceCounts
{
    public required int ActiveRules { get; init; }
    public required int PromotedRules { get; init; }
    public required int PendingRules { get; init; }
    public required int LessonCandidates { get; init; }
    public required int AcceptedRecommendations { get; init; }
    public required int Conflicts { get; init; }
    public required int TotalRetrievals { get; init; }
}

/// <summary>Tuning knobs for <see cref="IProjectDnaService"/>.</summary>
public sealed record ProjectDnaOptions
{
    /// <summary>Reference instant for recency/staleness; injected so reports are deterministic.</summary>
    public required DateTimeOffset AsOf { get; init; }

    /// <summary>How many items to surface per section.</summary>
    public int Top { get; init; } = 5;

    /// <summary>When set, only rules at this scope level are considered.</summary>
    public ScopeLevel? ScopeLevel { get; init; }

    /// <summary>When set (with a level), only rules whose scope value matches are considered.</summary>
    public string? ScopeValue { get; init; }

    /// <summary>A rule not retrieved within this many days is treated as stale.</summary>
    public int StaleDays { get; init; } = 90;
}
