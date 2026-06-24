using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Conflicts;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Dna;

/// <summary>
/// Default <see cref="IProjectDnaService"/>. Aggregates rules, the event ledger,
/// outcomes, mined lesson candidates, and accepted lifecycle recommendations into
/// a structured Project DNA report. Pure and deterministic: it reads from the
/// repositories, ranks with explicit tie-breakers (so output never depends on
/// storage order), and never calls any external service or model.
/// </summary>
public sealed class ProjectDnaService : IProjectDnaService
{
    /// <summary>Retrieval count at or above which a rule's retrieval bonus saturates.</summary>
    private const int RetrievalSaturation = 10;

    /// <summary>Confidence at or below which a rule is treated as low-confidence (risky).</summary>
    private const double LowConfidenceThreshold = 0.4;

    private readonly IRecallRuleRepository _rules;
    private readonly IRecallEventRepository _events;
    private readonly IRuleOutcomeRepository _outcomes;
    private readonly ILessonCandidateRepository _candidates;
    private readonly IRuleLifecycleRecommendationRepository _recommendations;
    private readonly IRuleConflictDetector _conflictDetector;

    public ProjectDnaService(
        IRecallRuleRepository rules,
        IRecallEventRepository events,
        IRuleOutcomeRepository outcomes,
        ILessonCandidateRepository candidates,
        IRuleLifecycleRecommendationRepository recommendations,
        IRuleConflictDetector conflictDetector)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _outcomes = outcomes ?? throw new ArgumentNullException(nameof(outcomes));
        _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _recommendations = recommendations ?? throw new ArgumentNullException(nameof(recommendations));
        _conflictDetector = conflictDetector ?? throw new ArgumentNullException(nameof(conflictDetector));
    }

    public async Task<ProjectDnaReport> GenerateAsync(ProjectDnaOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Top < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Top, "Top must be at least 1.");
        }

        var allRules = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        var events = await _events.ListAsync(cancellationToken).ConfigureAwait(false);
        var outcomes = await _outcomes.ListAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await _candidates.ListAsync(cancellationToken).ConfigureAwait(false);
        var recommendations = await _recommendations.ListAsync(cancellationToken).ConfigureAwait(false);

        // Restrict to the requested scope (if any) before any analysis.
        var scoped = allRules.Where(r => MatchesScope(r, options)).ToList();

        // Retrieval frequency per rule from the RuleApplied ledger.
        var retrievalCounts = events
            .Where(e => e.Type == RecallEventType.RuleApplied && e.RuleId is not null)
            .GroupBy(e => e.RuleId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        // Accepted/applied lifecycle recommendations by rule id (a directional signal).
        var acceptedRecs = recommendations
            .Where(r => r.Status is RecommendationStatus.Accepted or RecommendationStatus.Applied)
            .ToList();
        var recByRule = acceptedRecs
            .GroupBy(r => r.RuleId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.RecommendationType).ToList());

        // Per-rule outcome aggregates.
        var outcomeByRule = outcomes
            .GroupBy(o => o.RuleId)
            .ToDictionary(
                g => g.Key,
                g => new OutcomeAgg(
                    Positive: g.Count(o => o.ConfidenceDelta > 0),
                    Negative: g.Count(o => o.ConfidenceDelta < 0),
                    Net: g.Sum(o => o.ConfidenceDelta),
                    Corrections: g.Count(o => o.Type == OutcomeType.CorrectionRepeated)));

        // Rules eligible to feed the convention/pattern sections: in force only.
        // This is where requirement F lives — Archived, Superseded, Retired, Draft,
        // and Pending rules are excluded from the distilled conventions.
        var inForce = scoped
            .Where(r => r.Status is RuleStatus.Active or RuleStatus.Promoted && !r.Deprecated)
            .ToList();

        // Conflicts among the in-force corpus (used for risky-knowledge signals).
        var conflicts = _conflictDetector.Detect(inForce);
        var conflictCounts = conflicts
            .SelectMany(c => c.RuleIds)
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        // Build a scored view of every in-force rule once; sections filter and rank it.
        var scoredById = inForce.ToDictionary(
            r => r.Id,
            r =>
            {
                var retrievals = retrievalCounts.GetValueOrDefault(r.Id);
                var outcome = outcomeByRule.GetValueOrDefault(r.Id, OutcomeAgg.Empty);
                var recs = recByRule.GetValueOrDefault(r.Id) ?? [];
                var conflictCount = conflictCounts.GetValueOrDefault(r.Id);
                return new ScoredRule(
                    Rule: r,
                    Retrievals: retrievals,
                    Outcome: outcome,
                    Recommendations: recs,
                    ConflictCount: conflictCount,
                    Score: Score(r, retrievals, outcome, recs, options));
            });

        var scoredRules = scoredById.Values.ToList();

        var sections = new List<DnaSection>
        {
            BuildCorePrinciples(scoredRules, options),
            BuildRepositoryConventions(scoredRules, options),
            BuildKeywordSection(SectionKeys.Testing, SectionTitles.Testing, scoredRules, TestingKeywords, options),
            BuildKeywordSection(SectionKeys.Architecture, SectionTitles.Architecture, scoredRules, ArchitectureKeywords, options),
            BuildKeywordSection(SectionKeys.ErrorHandling, SectionTitles.ErrorHandling, scoredRules, ErrorHandlingKeywords, options),
            BuildKeywordSection(SectionKeys.Security, SectionTitles.Security, scoredRules, SecurityKeywords, options),
            BuildCommonMistakes(scoredRules, candidates, options),
            BuildAgentWarnings(scoredRules, options),
            BuildStaleOrRisky(scoredRules, options),
        };

        return new ProjectDnaReport
        {
            GeneratedAt = options.AsOf,
            Scope = new DnaScope { Level = options.ScopeLevel, Value = options.ScopeValue },
            Sections = sections,
            SourceCounts = new DnaSourceCounts
            {
                ActiveRules = scoped.Count(r => r.Status == RuleStatus.Active && !r.Deprecated),
                PromotedRules = scoped.Count(r => r.Status == RuleStatus.Promoted && !r.Deprecated),
                PendingRules = scoped.Count(r => r.Status == RuleStatus.Pending),
                LessonCandidates = candidates.Count(c => c.Status is LessonCandidateStatus.Suggested or LessonCandidateStatus.Accepted),
                AcceptedRecommendations = acceptedRecs.Count,
                Conflicts = conflicts.Count,
                TotalRetrievals = inForce.Sum(r => retrievalCounts.GetValueOrDefault(r.Id)),
            },
        };
    }

    // ---- Sections -----------------------------------------------------------

    /// <summary>
    /// The highest-confidence, most broadly applicable lessons: reusable
    /// engineering lessons and broadly-scoped (Global/Language) guidance.
    /// </summary>
    private static DnaSection BuildCorePrinciples(IReadOnlyList<ScoredRule> rules, ProjectDnaOptions options)
    {
        var items = rules
            .Where(IsPrinciple)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Rule.Id)
            .Take(options.Top)
            .Select(ToItem)
            .ToList();
        return new DnaSection { Key = SectionKeys.CorePrinciples, Title = SectionTitles.CorePrinciples, Items = items };
    }

    /// <summary>Repo-specific "when X, do Y" conventions.</summary>
    private static DnaSection BuildRepositoryConventions(IReadOnlyList<ScoredRule> rules, ProjectDnaOptions options)
    {
        var items = rules
            .Where(s => !IsPrinciple(s))
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Rule.Id)
            .Take(options.Top)
            .Select(ToItem)
            .ToList();
        return new DnaSection { Key = SectionKeys.RepositoryConventions, Title = SectionTitles.RepositoryConventions, Items = items };
    }

    private static DnaSection BuildKeywordSection(
        string key,
        string title,
        IReadOnlyList<ScoredRule> rules,
        IReadOnlyList<string> keywords,
        ProjectDnaOptions options)
    {
        var items = rules
            .Where(s => MatchesAnyKeyword(s.Rule, keywords))
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Rule.Id)
            .Take(options.Top)
            .Select(ToItem)
            .ToList();
        return new DnaSection { Key = key, Title = title, Items = items };
    }

    /// <summary>
    /// Frequently corrected or mined lessons. Draws from mined lesson candidates
    /// and from in-force rules that have a recorded repeated correction.
    /// </summary>
    private static DnaSection BuildCommonMistakes(
        IReadOnlyList<ScoredRule> rules,
        IReadOnlyList<LessonCandidate> candidates,
        ProjectDnaOptions options)
    {
        var fromCandidates = candidates
            .Where(c => c.Status is LessonCandidateStatus.Suggested or LessonCandidateStatus.Accepted)
            .OrderByDescending(c => c.OccurrenceCount)
            .ThenByDescending(c => c.Confidence)
            .ThenBy(c => c.Id)
            .Select(c => new DnaItem
            {
                Text = string.IsNullOrWhiteSpace(c.Title) ? c.SuggestedRule : c.Title,
                RuleIds = [],
                Confidence = Math.Round(c.Confidence, 2),
                Category = c.Category.ToString(),
                Evidence = BuildCandidateEvidence(c),
            });

        var fromCorrections = rules
            .Where(s => s.Outcome.Corrections > 0 && !string.IsNullOrWhiteSpace(s.Rule.Mistake))
            .OrderByDescending(s => s.Outcome.Corrections)
            .ThenByDescending(s => s.Score)
            .ThenBy(s => s.Rule.Id)
            .Select(s => new DnaItem
            {
                Text = s.Rule.Mistake,
                RuleIds = [s.Rule.Id],
                Confidence = Math.Round(s.Rule.Confidence, 2),
                Category = s.Rule.Category.ToString(),
                Evidence = BuildEvidence(s, options),
            });

        // Mined candidates first (they are explicitly recurring), then repeated corrections.
        var items = fromCandidates.Concat(fromCorrections).Take(options.Top).ToList();
        return new DnaSection { Key = SectionKeys.CommonMistakes, Title = SectionTitles.CommonMistakes, Items = items };
    }

    /// <summary>High-impact anti-patterns to avoid (warning-phrased or repeatedly failing rules).</summary>
    private static DnaSection BuildAgentWarnings(IReadOnlyList<ScoredRule> rules, ProjectDnaOptions options)
    {
        var items = rules
            .Where(s => IsAntiPattern(s.Rule) || s.Outcome.Negative >= 2 || s.Outcome.Corrections > 0)
            .OrderByDescending(s => s.Retrievals)
            .ThenByDescending(s => s.Score)
            .ThenBy(s => s.Rule.Id)
            .Take(options.Top)
            .Select(ToItem)
            .ToList();
        return new DnaSection { Key = SectionKeys.AgentWarnings, Title = SectionTitles.AgentWarnings, Items = items };
    }

    /// <summary>Low-confidence, stale, conflict-prone, or flagged-for-review guidance.</summary>
    private static DnaSection BuildStaleOrRisky(IReadOnlyList<ScoredRule> rules, ProjectDnaOptions options)
    {
        var staleBefore = options.AsOf.AddDays(-options.StaleDays);

        var items = rules
            .Where(s =>
                s.Rule.Confidence <= LowConfidenceThreshold
                || (s.Rule.LastUsedAt ?? s.Rule.CreatedAt) < staleBefore
                || s.ConflictCount > 0
                || s.Recommendations.Any(t => t is RecommendationType.Review or RecommendationType.LowerConfidence or RecommendationType.Archive or RecommendationType.Supersede))
            // Riskiest first: lowest confidence, then most conflicts, then oldest use.
            .OrderBy(s => s.Rule.Confidence)
            .ThenByDescending(s => s.ConflictCount)
            .ThenBy(s => s.Rule.LastUsedAt ?? s.Rule.CreatedAt)
            .ThenBy(s => s.Rule.Id)
            .Take(options.Top)
            .Select(s => ToItem(s, options))
            .ToList();
        return new DnaSection { Key = SectionKeys.StaleOrRisky, Title = SectionTitles.StaleOrRisky, Items = items };
    }

    // ---- Ranking ------------------------------------------------------------

    /// <summary>
    /// Deterministic DNA ranking. Combines, in fixed weights: rule status
    /// (Promoted &gt; Active &gt; Pending), confidence, bounded retrieval frequency,
    /// recency, outcome evidence, and accepted lifecycle-recommendation signals.
    /// </summary>
    private static double Score(
        RecallRule rule,
        int retrievals,
        OutcomeAgg outcome,
        IReadOnlyList<RecommendationType> recs,
        ProjectDnaOptions options)
    {
        var statusWeight = rule.Status switch
        {
            RuleStatus.Promoted => 2.0,
            RuleStatus.Active => 1.0,
            RuleStatus.Pending => 0.5,
            _ => 0.0,
        };

        var confidence = Math.Clamp(rule.Confidence, 0.0, 1.0);
        var retrievalBonus = Math.Min(retrievals, RetrievalSaturation) / (double)RetrievalSaturation * 0.5;

        var recencyBonus = 0.0;
        if (rule.LastUsedAt is { } lastUsed && lastUsed >= options.AsOf.AddDays(-options.StaleDays))
        {
            recencyBonus = 0.25;
        }

        var outcomeBonus = Math.Clamp(outcome.Net, -0.5, 0.5) * 0.5;

        var lifecycleBonus = 0.0;
        foreach (var type in recs)
        {
            lifecycleBonus += type switch
            {
                RecommendationType.Promote or RecommendationType.RaiseConfidence => 0.25,
                RecommendationType.Review or RecommendationType.LowerConfidence or RecommendationType.Archive or RecommendationType.Supersede => -0.25,
                _ => 0.0,
            };
        }

        return statusWeight + confidence + retrievalBonus + recencyBonus + outcomeBonus + lifecycleBonus;
    }

    // ---- Item / evidence construction --------------------------------------

    private static DnaItem ToItem(ScoredRule s) => ToItem(s, DefaultEvidenceOptions);

    private static DnaItem ToItem(ScoredRule s, ProjectDnaOptions options) => new()
    {
        Text = s.Rule.RuleText,
        RuleIds = [s.Rule.Id],
        Confidence = Math.Round(s.Rule.Confidence, 2),
        Category = s.Rule.Category.ToString(),
        Evidence = BuildEvidence(s, options),
    };

    private static IReadOnlyList<string> BuildEvidence(ScoredRule s, ProjectDnaOptions options)
    {
        var evidence = new List<string>
        {
            $"status: {s.Rule.Status}",
            $"confidence: {s.Rule.Confidence:0.00}",
        };

        if (s.Retrievals > 0)
        {
            evidence.Add($"retrieved {s.Retrievals}x");
        }

        if (s.Rule.LastUsedAt is { } lastUsed)
        {
            var days = Math.Max(0, (int)(options.AsOf - lastUsed).TotalDays);
            evidence.Add($"last used {days}d ago");
        }
        else
        {
            evidence.Add("never retrieved");
        }

        if (s.Outcome.Positive > 0)
        {
            evidence.Add($"{s.Outcome.Positive} positive outcome(s)");
        }

        if (s.Outcome.Negative > 0)
        {
            evidence.Add($"{s.Outcome.Negative} negative outcome(s)");
        }

        if (s.ConflictCount > 0)
        {
            evidence.Add($"in {s.ConflictCount} conflict(s)");
        }

        foreach (var type in s.Recommendations.OrderBy(t => t.ToString(), StringComparer.Ordinal))
        {
            evidence.Add($"recommendation: {type} accepted");
        }

        return evidence;
    }

    private static IReadOnlyList<string> BuildCandidateEvidence(LessonCandidate c)
    {
        var evidence = new List<string>
        {
            "mined lesson",
            $"confidence: {c.Confidence:0.00}",
            $"seen {c.OccurrenceCount}x",
        };
        if (c.Status == LessonCandidateStatus.Accepted)
        {
            evidence.Add("accepted");
        }

        return evidence;
    }

    // ---- Classification helpers --------------------------------------------

    /// <summary>
    /// A "core principle" is a reusable engineering lesson, or — when uncategorised
    /// — a broadly-scoped (Global/Language) rule. Everything else is a convention.
    /// </summary>
    private static bool IsPrinciple(ScoredRule s) =>
        s.Rule.Category == RuleCategory.EngineeringLesson
        || (s.Rule.Category == RuleCategory.Unknown && s.Rule.ScopeLevel is ScopeLevel.Global or ScopeLevel.Language);

    private static bool MatchesAnyKeyword(RecallRule rule, IReadOnlyList<string> keywords)
    {
        var haystack = Haystack(rule);
        foreach (var keyword in keywords)
        {
            if (haystack.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the rule's <em>actionable guidance</em> is phrased as a prohibition.
    /// Only <see cref="RecallRule.RuleText"/> is scanned — not the auto-generated
    /// <see cref="RecallRule.Mistake"/>, which the extractor prefixes with "Avoid …"
    /// for every rule and would otherwise mark the whole corpus as a warning.
    /// </summary>
    private static bool IsAntiPattern(RecallRule rule)
    {
        var text = rule.RuleText.ToLowerInvariant();
        foreach (var marker in AntiPatternMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lower-cased searchable text for a rule (all human-authored fields plus tags).</summary>
    private static string Haystack(RecallRule rule) =>
        string.Join(
            ' ',
            rule.Trigger,
            rule.Mistake,
            rule.RuleText,
            rule.TechnicalContext,
            rule.Tags).ToLowerInvariant();

    private static bool MatchesScope(RecallRule rule, ProjectDnaOptions options)
    {
        if (options.ScopeLevel is not { } level)
        {
            return true;
        }

        if (rule.ScopeLevel != level)
        {
            return false;
        }

        return string.IsNullOrEmpty(options.ScopeValue)
            || string.Equals(rule.ScopeValue, options.ScopeValue, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Static data --------------------------------------------------------

    // Evidence rendering needs an AsOf; when an item is built outside a risky/stale
    // context we still want deterministic "days ago" math, so reuse a fixed default.
    private static readonly ProjectDnaOptions DefaultEvidenceOptions =
        new() { AsOf = DateTimeOffset.UnixEpoch };

    private static readonly string[] TestingKeywords =
        ["test", "xunit", "nunit", "mstest", "moq", "mock", "stub", "fake", "assert", "fixture", "fluentassertions", "theory", "[fact]", "coverage", "tdd", "snapshot", "it.isany"];

    private static readonly string[] ArchitectureKeywords =
        ["architecture", "architectural", "layer", "design", "pattern", "dependency injection", "mediatr", "cqrs", "boundary", "coupling", "cohesion", "abstraction", "interface", "solid", "decoupl", "module"];

    private static readonly string[] ErrorHandlingKeywords =
        ["exception", "error", "result<", "result type", "throw", "catch", "try", "fail", "validation", "validate", "nullable", "null reference", "guard", "defensive", "domain failure"];

    private static readonly string[] SecurityKeywords =
        ["gate", "feature flag", "feature limit", "auth", "authz", "authn", "authorization", "authentication", "permission", "security", "secure", "access control", "role", "token", "credential", "secret", "sanitiz", "injection", "xss", "csrf"];

    private static readonly string[] AntiPatternMarkers =
        ["avoid", "never", "don't", "do not", "must not", "should not", "anti-pattern", "antipattern"];

    private sealed record OutcomeAgg(int Positive, int Negative, double Net, int Corrections)
    {
        public static readonly OutcomeAgg Empty = new(0, 0, 0.0, 0);
    }

    private sealed record ScoredRule(
        RecallRule Rule,
        int Retrievals,
        OutcomeAgg Outcome,
        IReadOnlyList<RecommendationType> Recommendations,
        int ConflictCount,
        double Score);
}

/// <summary>Stable machine keys for the DNA sections.</summary>
public static class SectionKeys
{
    public const string CorePrinciples = "core-principles";
    public const string RepositoryConventions = "repository-conventions";
    public const string Testing = "testing-patterns";
    public const string Architecture = "architecture-patterns";
    public const string ErrorHandling = "error-handling";
    public const string Security = "security";
    public const string CommonMistakes = "common-mistakes";
    public const string AgentWarnings = "agent-warnings";
    public const string StaleOrRisky = "stale-or-risky";
}

/// <summary>Human-readable titles for the DNA sections.</summary>
public static class SectionTitles
{
    public const string CorePrinciples = "Core Principles";
    public const string RepositoryConventions = "Repository Conventions";
    public const string Testing = "Testing Patterns";
    public const string Architecture = "Architecture Patterns";
    public const string ErrorHandling = "Error Handling";
    public const string Security = "Feature Gates / Authorization / Security";
    public const string CommonMistakes = "Common Mistakes";
    public const string AgentWarnings = "Agent Warnings";
    public const string StaleOrRisky = "Stale or Risky Knowledge";
}
