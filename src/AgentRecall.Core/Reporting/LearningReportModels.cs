namespace AgentRecall.Core.Reporting;

/// <summary>
/// A learning report for a single calendar month. Counts are attributed to the
/// month via the event ledger (promotions, supersessions, rejections) and the
/// rules captured in the period; the knowledge-base snapshot metrics
/// (confidence, most-retrieved, most-common category) reflect those lessons.
/// </summary>
public sealed record MonthlyLearningReport
{
    /// <summary>Four-digit year the report covers.</summary>
    public required int Year { get; init; }

    /// <summary>Month (1-12) the report covers.</summary>
    public required int Month { get; init; }

    /// <summary>Human-readable period, e.g. "June 2026".</summary>
    public required string Period { get; init; }

    /// <summary>Rules first captured during the period.</summary>
    public required int LessonsCaptured { get; init; }

    /// <summary>Rules promoted during the period (from the event ledger).</summary>
    public required int LessonsPromoted { get; init; }

    /// <summary>Rules superseded during the period (from the event ledger).</summary>
    public required int LessonsSuperseded { get; init; }

    /// <summary>Candidates rejected as not memory-worthy during the period.</summary>
    public required int LessonsRejected { get; init; }

    /// <summary>Distinct rules retrieved at least the frequent-use threshold of times in the period.</summary>
    public required int FrequentlyUsedRules { get; init; }

    /// <summary>Average confidence of the lessons captured in the period (0 when none).</summary>
    public required double AverageConfidence { get; init; }

    /// <summary>The rule retrieved most often in the period, or null when there were no retrievals.</summary>
    public RetrievedRuleStat? MostRetrievedRule { get; init; }

    /// <summary>The most common tag among lessons captured in the period, or null when untagged.</summary>
    public string? MostCommonCategory { get; init; }
}

/// <summary>Cradle-to-grave counts across the whole rule corpus.</summary>
public sealed record RuleLifecycleReport
{
    /// <summary>Every rule ever created.</summary>
    public required int Created { get; init; }

    /// <summary>Rules currently in the Promoted state.</summary>
    public required int Promoted { get; init; }

    /// <summary>Rules currently in the Superseded state.</summary>
    public required int Superseded { get; init; }

    /// <summary>Rules currently in the Archived state.</summary>
    public required int Archived { get; init; }

    /// <summary>Candidates rejected as not memory-worthy (from the event ledger).</summary>
    public required int Rejected { get; init; }

    /// <summary>Rules currently usable — Active or Promoted.</summary>
    public required int StillActive { get; init; }
}

/// <summary>How AgentRecall is being used: retrieval, value, growth, and staleness.</summary>
public sealed record LearningUsageReport
{
    /// <summary>Rules ordered by how often retrieval surfaced them.</summary>
    public required IReadOnlyList<RetrievedRuleStat> TopRetrievedRules { get; init; }

    /// <summary>Rules ordered by value score (retrieval count × confidence).</summary>
    public required IReadOnlyList<ValuableLessonStat> MostValuableLessons { get; init; }

    /// <summary>Cumulative rule count at the end of each month, oldest first.</summary>
    public required IReadOnlyList<KnowledgeGrowthPoint> KnowledgeGrowth { get; init; }

    /// <summary>Active rules that have not been retrieved for a long time.</summary>
    public required IReadOnlyList<StaleRuleStat> StaleRules { get; init; }
}

/// <summary>The project's distilled conventions, for fast onboarding.</summary>
public sealed record ProjectDnaReport
{
    /// <summary>Top conventions, highest-value first.</summary>
    public required IReadOnlyList<DnaConvention> TopConventions { get; init; }

    /// <summary>The most common tags across the active corpus, most frequent first.</summary>
    public required IReadOnlyList<CategoryCount> CoreCategories { get; init; }
}

/// <summary>A rule and how many times retrieval returned it.</summary>
public sealed record RetrievedRuleStat
{
    public required int RuleId { get; init; }
    public required string RuleText { get; init; }
    public required int RetrievalCount { get; init; }
}

/// <summary>A rule scored by usefulness: retrieval count × confidence.</summary>
public sealed record ValuableLessonStat
{
    public required int RuleId { get; init; }
    public required string RuleText { get; init; }
    public required int RetrievalCount { get; init; }
    public required double Confidence { get; init; }
    public required double Score { get; init; }
}

/// <summary>Cumulative rule count at the end of a single month.</summary>
public sealed record KnowledgeGrowthPoint
{
    public required int Year { get; init; }
    public required int Month { get; init; }
    public required string Period { get; init; }
    public required int CumulativeRules { get; init; }
}

/// <summary>An active rule that may be obsolete because it is rarely retrieved.</summary>
public sealed record StaleRuleStat
{
    public required int RuleId { get; init; }
    public required string RuleText { get; init; }
    public required double Confidence { get; init; }

    /// <summary>Days since the rule was last retrieved, or null if it never has been.</summary>
    public int? DaysSinceLastRetrieved { get; init; }
}

/// <summary>A single distilled convention.</summary>
public sealed record DnaConvention
{
    public required int Rank { get; init; }
    public required int RuleId { get; init; }
    public required string RuleText { get; init; }
}

/// <summary>A tag and how many active rules carry it.</summary>
public sealed record CategoryCount
{
    public required string Category { get; init; }
    public required int Count { get; init; }
}

/// <summary>Tuning knobs for the usage report.</summary>
public sealed record UsageReportOptions
{
    /// <summary>Reference instant for staleness; injected so reports are deterministic.</summary>
    public required DateTimeOffset AsOf { get; init; }

    /// <summary>How many rules to list in the top-retrieved and most-valuable sections.</summary>
    public int Top { get; init; } = 10;

    /// <summary>A rule not retrieved within this many days is considered potentially stale.</summary>
    public int StaleDays { get; init; } = 90;
}
