using System.Globalization;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Reporting;

/// <summary>
/// Default <see cref="ILearningReportService"/>. Aggregates the local rule corpus
/// and event ledger into reports. Pure and deterministic: it reads from the
/// repositories, sorts with explicit tie-breakers (so output never depends on
/// storage order), and never calls out to any external service.
/// </summary>
public sealed class LearningReportService : ILearningReportService
{
    /// <summary>Retrievals in a period at or above which a rule counts as "frequently used".</summary>
    public const int FrequentUseThreshold = 3;

    private readonly IRecallRuleRepository _rules;
    private readonly IRecallEventRepository _events;
    private readonly IRuleOutcomeRepository _outcomes;
    private readonly Conflicts.IRuleConflictDetector _conflictDetector;

    public LearningReportService(
        IRecallRuleRepository rules,
        IRecallEventRepository events,
        IRuleOutcomeRepository outcomes,
        Conflicts.IRuleConflictDetector conflictDetector)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _outcomes = outcomes ?? throw new ArgumentNullException(nameof(outcomes));
        _conflictDetector = conflictDetector ?? throw new ArgumentNullException(nameof(conflictDetector));
    }

    public async Task<MonthlyLearningReport> GetMonthlyReportAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        if (month is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(month), month, "Month must be between 1 and 12.");
        }

        var start = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMonths(1);

        var rules = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        var events = await _events.ListAsync(cancellationToken).ConfigureAwait(false);

        var captured = rules.Where(r => InPeriod(r.CreatedAt, start, end)).ToList();

        var retrievalsInPeriod = events
            .Where(e => e.Type == RecallEventType.RuleApplied && e.RuleId is not null && InPeriod(e.CreatedAt, start, end))
            .GroupBy(e => e.RuleId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var rulesById = rules.ToDictionary(r => r.Id);

        var outcomes = await _outcomes.ListAsync(cancellationToken).ConfigureAwait(false);
        var outcomesInPeriod = outcomes.Where(o => InPeriod(o.CreatedAt, start, end)).ToList();
        var netByRule = outcomesInPeriod
            .GroupBy(o => o.RuleId)
            .Select(g => new OutcomeRuleStat
            {
                RuleId = g.Key,
                RuleText = rulesById.TryGetValue(g.Key, out var r) ? r.RuleText : $"(rule #{g.Key})",
                NetConfidenceChange = Math.Round(g.Sum(o => o.ConfidenceDelta), 2),
                OutcomeCount = g.Count(),
            })
            .ToList();

        RetrievedRuleStat? mostRetrieved = null;
        foreach (var (ruleId, count) in retrievalsInPeriod
                     .OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key))
        {
            if (rulesById.TryGetValue(ruleId, out var rule))
            {
                mostRetrieved = new RetrievedRuleStat { RuleId = ruleId, RuleText = rule.RuleText, RetrievalCount = count };
                break;
            }
        }

        return new MonthlyLearningReport
        {
            Year = year,
            Month = month,
            Period = start.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            LessonsCaptured = captured.Count,
            LessonsPromoted = CountEvents(events, RecallEventType.RulePromoted, start, end),
            LessonsSuperseded = CountEvents(events, RecallEventType.RuleSuperseded, start, end),
            LessonsRejected = CountEvents(events, RecallEventType.RuleRejected, start, end),
            FrequentlyUsedRules = retrievalsInPeriod.Count(kv => kv.Value >= FrequentUseThreshold),
            AverageConfidence = captured.Count == 0 ? 0.0 : Math.Round(captured.Average(r => r.Confidence), 2),
            MostRetrievedRule = mostRetrieved,
            MostCommonCategory = MostCommonCategory(captured),
            PositiveOutcomes = outcomesInPeriod.Count(o => o.ConfidenceDelta > 0),
            NegativeOutcomes = outcomesInPeriod.Count(o => o.ConfidenceDelta < 0),
            NetConfidenceChange = Math.Round(outcomesInPeriod.Sum(o => o.ConfidenceDelta), 2),
            MostImprovedRules = netByRule
                .Where(s => s.NetConfidenceChange > 0)
                .OrderByDescending(s => s.NetConfidenceChange)
                .ThenBy(s => s.RuleId)
                .Take(3)
                .ToList(),
            MostDegradedRules = netByRule
                .Where(s => s.NetConfidenceChange < 0)
                .OrderBy(s => s.NetConfidenceChange)
                .ThenBy(s => s.RuleId)
                .Take(3)
                .ToList(),
        };
    }

    public async Task<RuleLifecycleReport> GetLifecycleReportAsync(CancellationToken cancellationToken = default)
    {
        var rules = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        var events = await _events.ListAsync(cancellationToken).ConfigureAwait(false);

        return new RuleLifecycleReport
        {
            Created = rules.Count,
            Promoted = rules.Count(r => r.Status == RuleStatus.Promoted),
            Superseded = rules.Count(r => r.Status == RuleStatus.Superseded),
            Archived = rules.Count(r => r.Status == RuleStatus.Archived),
            Rejected = events.Count(e => e.Type == RecallEventType.RuleRejected),
            StillActive = rules.Count(r => r.Status is RuleStatus.Active or RuleStatus.Promoted),
        };
    }

    public async Task<LearningUsageReport> GetUsageReportAsync(UsageReportOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var rules = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        var events = await _events.ListAsync(cancellationToken).ConfigureAwait(false);

        var retrievalCounts = events
            .Where(e => e.Type == RecallEventType.RuleApplied && e.RuleId is not null)
            .GroupBy(e => e.RuleId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var topRetrieved = rules
            .Select(r => (Rule: r, Count: retrievalCounts.GetValueOrDefault(r.Id)))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Rule.Id)
            .Take(options.Top)
            .Select(x => new RetrievedRuleStat { RuleId = x.Rule.Id, RuleText = x.Rule.RuleText, RetrievalCount = x.Count })
            .ToList();

        var mostValuable = rules
            .Select(r => (Rule: r, Count: retrievalCounts.GetValueOrDefault(r.Id)))
            .Where(x => x.Count > 0)
            .Select(x => new ValuableLessonStat
            {
                RuleId = x.Rule.Id,
                RuleText = x.Rule.RuleText,
                RetrievalCount = x.Count,
                Confidence = Math.Round(x.Rule.Confidence, 2),
                Score = Math.Round(x.Count * x.Rule.Confidence, 2),
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.RuleId)
            .Take(options.Top)
            .ToList();

        // Conflicts among the in-force corpus, ranked by how many each rule joins.
        var inForce = rules.Where(r => !r.Deprecated && r.Status is RuleStatus.Active or RuleStatus.Promoted).ToList();
        var conflicts = _conflictDetector.Detect(inForce);
        var rulesById = rules.ToDictionary(r => r.Id);
        var conflictCounts = conflicts
            .SelectMany(c => c.RuleIds)
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        var topConflicting = conflictCounts
            .Where(kv => rulesById.ContainsKey(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(options.Top)
            .Select(kv => new ConflictingRuleStat
            {
                RuleId = kv.Key,
                RuleText = rulesById[kv.Key].RuleText,
                ConflictCount = kv.Value,
            })
            .ToList();

        // Outcome-driven effectiveness across all recorded outcomes.
        var outcomes = await _outcomes.ListAsync(cancellationToken).ConfigureAwait(false);
        var outcomeStats = outcomes
            .GroupBy(o => o.RuleId)
            .Select(g => new
            {
                RuleId = g.Key,
                Net = Math.Round(g.Sum(o => o.ConfidenceDelta), 2),
                Count = g.Count(),
                Negatives = g.Count(o => o.ConfidenceDelta < 0),
            })
            .Where(s => rulesById.ContainsKey(s.RuleId))
            .ToList();

        OutcomeRuleStat ToStat(int ruleId, double net, int count) => new()
        {
            RuleId = ruleId,
            RuleText = rulesById[ruleId].RuleText,
            NetConfidenceChange = net,
            OutcomeCount = count,
        };

        var mostEffective = outcomeStats
            .Where(s => s.Net > 0)
            .OrderByDescending(s => s.Net)
            .ThenBy(s => s.RuleId)
            .Take(options.Top)
            .Select(s => ToStat(s.RuleId, s.Net, s.Count))
            .ToList();

        var repeatedNegative = outcomeStats
            .Where(s => s.Negatives >= 2)
            .OrderByDescending(s => s.Negatives)
            .ThenBy(s => s.RuleId)
            .Take(options.Top)
            .Select(s => ToStat(s.RuleId, s.Net, s.Count))
            .ToList();

        var validatedRuleIds = outcomes.Select(o => o.RuleId).ToHashSet();
        var rarelyValidated = rules
            .Select(r => (Rule: r, Count: retrievalCounts.GetValueOrDefault(r.Id)))
            .Where(x => x.Count >= FrequentUseThreshold && !validatedRuleIds.Contains(x.Rule.Id))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Rule.Id)
            .Take(options.Top)
            .Select(x => new RetrievedRuleStat { RuleId = x.Rule.Id, RuleText = x.Rule.RuleText, RetrievalCount = x.Count })
            .ToList();

        return new LearningUsageReport
        {
            TopRetrievedRules = topRetrieved,
            MostValuableLessons = mostValuable,
            KnowledgeGrowth = BuildKnowledgeGrowth(rules),
            StaleRules = BuildStaleRules(rules, options),
            TotalConflicts = conflicts.Count,
            TopConflictingRules = topConflicting,
            MostEffectiveRules = mostEffective,
            RulesWithRepeatedNegativeOutcomes = repeatedNegative,
            FrequentlyRetrievedButRarelyValidated = rarelyValidated,
        };
    }

    public async Task<ProjectDnaReport> GetDnaReportAsync(int top = 5, CancellationToken cancellationToken = default)
    {
        if (top < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(top), top, "Top must be at least 1.");
        }

        var rules = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        var events = await _events.ListAsync(cancellationToken).ConfigureAwait(false);

        var retrievalCounts = events
            .Where(e => e.Type == RecallEventType.RuleApplied && e.RuleId is not null)
            .GroupBy(e => e.RuleId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var active = rules
            .Where(r => r.Status is RuleStatus.Active or RuleStatus.Promoted && !r.Deprecated)
            .ToList();

        var conventions = active
            .OrderByDescending(r => DnaValue(r, retrievalCounts.GetValueOrDefault(r.Id)))
            .ThenBy(r => r.Id)
            .Take(top)
            .Select((r, index) => new DnaConvention { Rank = index + 1, RuleId = r.Id, RuleText = r.RuleText })
            .ToList();

        var coreCategories = active
            .SelectMany(ParseTags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CategoryCount { Category = g.OrderBy(t => t, StringComparer.Ordinal).First(), Count = g.Count() })
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Category, StringComparer.Ordinal)
            .Take(top)
            .ToList();

        return new ProjectDnaReport { TopConventions = conventions, CoreCategories = coreCategories };
    }

    /// <summary>
    /// A rule's standing as a project convention: confidence, with a lift for
    /// promoted rules and a bounded bonus for retrieval frequency.
    /// </summary>
    private static double DnaValue(RecallRule rule, int retrievalCount)
    {
        var statusBonus = rule.Status == RuleStatus.Promoted ? 0.15 : 0.0;
        var retrievalBonus = Math.Min(retrievalCount, 10) / 10.0 * 0.5;
        return Math.Clamp(rule.Confidence, 0.0, 1.0) + statusBonus + retrievalBonus;
    }

    private static IReadOnlyList<KnowledgeGrowthPoint> BuildKnowledgeGrowth(IReadOnlyList<RecallRule> rules)
    {
        if (rules.Count == 0)
        {
            return [];
        }

        var ordered = rules.OrderBy(r => r.CreatedAt).ToList();
        var firstMonth = MonthStart(ordered[0].CreatedAt);
        var lastMonth = MonthStart(ordered[^1].CreatedAt);

        var points = new List<KnowledgeGrowthPoint>();
        for (var cursor = firstMonth; cursor <= lastMonth; cursor = cursor.AddMonths(1))
        {
            var monthEnd = cursor.AddMonths(1);
            var cumulative = ordered.Count(r => r.CreatedAt < monthEnd);
            points.Add(new KnowledgeGrowthPoint
            {
                Year = cursor.Year,
                Month = cursor.Month,
                Period = cursor.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                CumulativeRules = cumulative,
            });
        }

        return points;
    }

    private static IReadOnlyList<StaleRuleStat> BuildStaleRules(IReadOnlyList<RecallRule> rules, UsageReportOptions options)
    {
        var staleBefore = options.AsOf.AddDays(-options.StaleDays);

        return rules
            .Where(r => r.Status is RuleStatus.Active or RuleStatus.Promoted)
            .Where(r => (r.LastUsedAt ?? r.CreatedAt) < staleBefore)
            .OrderBy(r => r.LastUsedAt ?? r.CreatedAt)
            .ThenBy(r => r.Confidence)
            .ThenBy(r => r.Id)
            .Take(options.Top)
            .Select(r => new StaleRuleStat
            {
                RuleId = r.Id,
                RuleText = r.RuleText,
                Confidence = Math.Round(r.Confidence, 2),
                DaysSinceLastRetrieved = r.LastUsedAt is null ? null : (int)(options.AsOf - r.LastUsedAt.Value).TotalDays,
            })
            .ToList();
    }

    private static string? MostCommonCategory(IReadOnlyList<RecallRule> rules) =>
        rules
            .SelectMany(ParseTags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(t => t, StringComparer.Ordinal).First())
            .FirstOrDefault();

    private static int CountEvents(IReadOnlyList<RecallEvent> events, RecallEventType type, DateTimeOffset start, DateTimeOffset end) =>
        events.Count(e => e.Type == type && InPeriod(e.CreatedAt, start, end));

    private static IEnumerable<string> ParseTags(RecallRule rule) =>
        string.IsNullOrWhiteSpace(rule.Tags)
            ? []
            : rule.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool InPeriod(DateTimeOffset value, DateTimeOffset start, DateTimeOffset end)
    {
        var utc = value.ToUniversalTime();
        return utc >= start && utc < end;
    }

    private static DateTimeOffset MonthStart(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
