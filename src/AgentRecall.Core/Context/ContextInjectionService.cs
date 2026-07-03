using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Policy;

namespace AgentRecall.Core.Context;

/// <summary>
/// Default <see cref="IContextInjectionService"/>. Ranks rules by usefulness using
/// blended relevance signals weighted by confidence, prunes conflicts and
/// superseded rules via the policy engine, buckets the survivors into
/// must-follow / warning / suggested, and fills a token budget highest-value
/// first.
/// </summary>
public sealed class ContextInjectionService : IContextInjectionService
{
    // Relevance signal weights (sum to 1.0).
    private const double KeywordWeight = 0.30;
    private const double SemanticWeight = 0.30;
    private const double DomainWeight = 0.20;
    private const double TaskTypeWeight = 0.10;
    private const double ScopeWeight = 0.10;

    /// <summary>Minimum relevance for a rule to be considered at all.</summary>
    private const double RelevanceFloor = 0.08;

    /// <summary>Score at or above which a high-trust rule is "must-follow".</summary>
    private const double MustFollowFloor = 0.15;

    /// <summary>
    /// Score multiplier applied to built-in seed rules so a locally learned rule of equal
    /// relevance always ranks above generic starter guidance. Repeated successful local use
    /// raises a seed rule's confidence, which lifts its score back up over time.
    /// </summary>
    private const double SeedScoreDampening = 0.85;

    /// <summary>
    /// Most seed rules injected per prompt when the task is not about tidying/refactoring,
    /// so starter guidance never floods the context. Lifted for tidy-focused tasks.
    /// </summary>
    private const int SeedInjectionCap = 2;

    /// <summary>Task words that signal a tidy/refactor prompt, where seed rules are on-topic.</summary>
    private static readonly HashSet<string> TidyTaskTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "tidy", "refactor", "refactoring", "cleanup", "clean", "rename", "extract",
        "guard", "readability", "restructure", "simplify", "nested", "conditional", "conditionals",
    };

    // Tokens/words associated with each task type, used for the task-type signal.
    private static readonly Dictionary<TaskType, string[]> TaskTypeTerms = new()
    {
        [TaskType.Security] = ["security", "auth", "authentication", "authorization", "injection", "secret", "credential", "encryption", "vulnerability", "sanitize", "validation"],
        [TaskType.Performance] = ["performance", "latency", "cache", "caching", "allocation", "async", "throughput", "memory", "index"],
        [TaskType.Refactor] = ["refactor", "design", "pattern", "structure", "coupling", "cohesion", "abstraction", "naming"],
        [TaskType.Test] = ["test", "tests", "mock", "assertion", "coverage", "fixture", "deterministic"],
        [TaskType.BugFix] = ["bug", "fix", "regression", "edge", "null", "boundary", "exception", "error"],
        [TaskType.Documentation] = ["documentation", "docs", "comment", "comments", "readme", "example"],
        [TaskType.Review] = ["review", "convention", "style", "consistency", "readability"],
    };

    private readonly IRecallRuleRepository _rules;
    private readonly IRecallEventRepository _events;
    private readonly IRetrievalRecordRepository _retrievals;
    private readonly IPolicyEngine _policy;
    private readonly IConceptExpander _concepts;
    private readonly Conflicts.IRuleConflictDetector _conflictDetector;
    private readonly Conflicts.IRuleResolutionService _resolution;

    public ContextInjectionService(
        IRecallRuleRepository rules,
        IRecallEventRepository events,
        IRetrievalRecordRepository retrievals,
        IPolicyEngine policy,
        IConceptExpander concepts,
        Conflicts.IRuleConflictDetector conflictDetector,
        Conflicts.IRuleResolutionService resolution)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _retrievals = retrievals ?? throw new ArgumentNullException(nameof(retrievals));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _concepts = concepts ?? throw new ArgumentNullException(nameof(concepts));
        _conflictDetector = conflictDetector ?? throw new ArgumentNullException(nameof(conflictDetector));
        _resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
    }

    public async Task<ContextInjectionResult> BuildContextAsync(ContextRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var taskTokens = ContextTokens.FromTask(request.Task);
        var domainTokens = ContextTokens.FromIdentifiers(request.FileNames.Concat(request.ChangedEntities));

        if (taskTokens.Count == 0 && domainTokens.Count == 0)
        {
            return Empty(request.TokenBudget, "No task keywords or changed entities to match on.");
        }

        // Concepts are activated by everything the task is "about".
        var concepts = _concepts.Build(taskTokens.Concat(domainTokens));

        var all = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        var pool = all.Where(RuleStatusSets.IsEffective).ToList();

        // Score every rule; keep those clearing the relevance floor.
        var assessments = new Dictionary<int, Assessment>();
        foreach (var rule in pool)
        {
            var assessment = Assess(rule, taskTokens, domainTokens, concepts, request);
            if (assessment.Relevance >= RelevanceFloor)
            {
                assessments[rule.Id] = assessment;
            }
        }

        // Let the policy engine prune conflicts, supersedes and overrides.
        var relevantRules = assessments.Values.Select(a => a.Rule).ToList();
        var resolution = _policy.Resolve(relevantRules, new PolicyContext
        {
            ScopeLevel = request.ScopeLevel,
            ScopeValue = request.ScopeValue,
        });

        var prunedByPolicy = resolution.Ignored.Count;

        var ranked = resolution.Effective
            .Select(v => assessments[v.Rule.Id])
            .ToList();

        // Optionally fold in Pending rules — scored the same way, but never elevated
        // to must-follow since they haven't been approved.
        if (request.IncludePending)
        {
            foreach (var rule in all.Where(r => r.Status == RuleStatus.Pending && !r.Deprecated))
            {
                var assessment = Assess(rule, taskTokens, domainTokens, concepts, request) with { Unapproved = true };
                if (assessment.Relevance >= RelevanceFloor)
                {
                    ranked.Add(assessment);
                }
            }
        }

        ranked = ranked
            .OrderByDescending(a => a.Score)
            .ThenByDescending(a => a.Rule.Confidence)
            .ThenBy(a => a.Rule.Id)
            .ToList();

        // Cap seed rules so starter guidance never floods the context — unless the task is
        // itself about tidying/refactoring, where seed rules are the point.
        ranked = CapSeedRules(ranked, taskTokens, request.TaskType);

        var result = PackIntoBudget(ranked, request.TokenBudget, request.Limit, prunedByPolicy);

        // Resolve conflicts among the rules that survived to injection. This catches
        // competing guidance the polarity-based policy does not (e.g. unit vs
        // integration tests): the loser is dropped and the conflict is recorded, so
        // the agent sees a single chosen rule plus an explanation.
        result = ResolveInjectedConflicts(result);

        if (request.RecordUsage)
        {
            var retrievalId = await RecordRetrievalAsync(request, result, cancellationToken).ConfigureAwait(false);
            result = result with { RetrievalId = retrievalId };
        }

        return result;
    }

    /// <summary>
    /// Detects conflicts among the injected rules, drops each loser, and records the
    /// resolution. Returns the result unchanged when nothing conflicts.
    /// </summary>
    private ContextInjectionResult ResolveInjectedConflicts(ContextInjectionResult result)
    {
        var injected = result.All.Select(r => r.Rule).ToList();
        if (injected.Count < 2)
        {
            return result;
        }

        var conflicts = _conflictDetector.Detect(injected);
        if (conflicts.Count == 0)
        {
            return result;
        }

        var byId = injected.ToDictionary(r => r.Id);
        var losers = new HashSet<int>();
        var resolved = new List<Conflicts.ResolvedConflict>();

        foreach (var conflict in conflicts)
        {
            var members = conflict.RuleIds.Select(id => byId[id]).ToList();

            // Skip a conflict whose rules were already settled by an earlier one.
            if (members.Any(m => losers.Contains(m.Id)))
            {
                continue;
            }

            var resolution = _resolution.Resolve(members);
            var selected = byId[resolution.SelectedRuleId];
            var ignored = members.Where(r => r.Id != selected.Id).ToList();

            foreach (var loser in ignored)
            {
                losers.Add(loser.Id);
            }

            resolved.Add(new Conflicts.ResolvedConflict
            {
                Conflict = conflict,
                Resolution = resolution,
                Selected = selected,
                Ignored = ignored,
            });
        }

        if (losers.Count == 0)
        {
            return result;
        }

        return result with
        {
            MustFollow = result.MustFollow.Where(i => !losers.Contains(i.Rule.Id)).ToList(),
            Warnings = result.Warnings.Where(i => !losers.Contains(i.Rule.Id)).ToList(),
            Suggested = result.Suggested.Where(i => !losers.Contains(i.Rule.Id)).ToList(),
            Conflicts = resolved,
        };
    }

    /// <summary>
    /// Records that the injected rules were retrieved: one RuleApplied event per
    /// rule, a LastUsedAt bump, and a retrieval record that ties this set of rules to
    /// a stable id so outcomes can be attached to them later. Returns that id, or
    /// null when nothing was injected.
    /// </summary>
    private async Task<string?> RecordRetrievalAsync(
        ContextRequest request,
        ContextInjectionResult result,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var retrieved = result.All
            .Select(injected => injected.Rule)
            .DistinctBy(rule => rule.Id)
            .ToList();

        if (retrieved.Count == 0)
        {
            return null;
        }

        // Batch the writes: one insert for all RuleApplied events and one update for all
        // LastUsedAt bumps, instead of an await-per-rule N+1 in this hot retrieval path.
        var events = retrieved
            .Select(rule => new RecallEvent
            {
                Type = RecallEventType.RuleApplied,
                RuleId = rule.Id,
                Trigger = "retrieval",
                Details = $"Rule #{rule.Id} retrieved for context injection.",
            })
            .ToList();
        await _events.AddRangeAsync(events, cancellationToken).ConfigureAwait(false);

        foreach (var rule in retrieved)
        {
            rule.LastUsedAt = now;
        }

        await _rules.UpdateRangeAsync(retrieved, cancellationToken).ConfigureAwait(false);

        var retrievalId = Guid.NewGuid().ToString("N")[..12];
        await _retrievals.AddAsync(new RetrievalRecord
        {
            RetrievalId = retrievalId,
            Task = request.Task,
            RuleIds = string.Join(",", retrieved.Select(r => r.Id)),
        }, cancellationToken).ConfigureAwait(false);

        return retrievalId;
    }

    private Assessment Assess(
        RecallRule rule,
        HashSet<string> taskTokens,
        HashSet<string> domainTokens,
        ConceptContext concepts,
        ContextRequest request)
    {
        var ruleTokens = ContextTokens.FromRule(rule);
        var reasons = new List<string>();

        // 1. Literal keyword overlap with the task.
        var keywordHits = taskTokens.Where(ruleTokens.Contains).ToList();
        var keyword = taskTokens.Count == 0 ? 0 : (double)keywordHits.Count / taskTokens.Count;
        if (keywordHits.Count > 0)
        {
            reasons.Add($"matches task keyword(s): {string.Join(", ", keywordHits)}");
        }

        // 2. Semantic match via activated concept groups (no shared words needed).
        var semanticGroups = new Dictionary<string, IReadOnlyCollection<string>>();
        var semanticHits = 0;
        foreach (var token in ruleTokens)
        {
            if (!taskTokens.Contains(token) && !domainTokens.Contains(token) &&
                concepts.TryRelate(token, out var group, out var viaSeeds))
            {
                semanticHits++;
                semanticGroups[group] = viaSeeds;
            }
        }

        var semantic = semanticGroups.Count == 0
            ? 0
            : Math.Min(1.0, 0.6 * semanticGroups.Count + 0.2 * (semanticHits - semanticGroups.Count));
        foreach (var (group, seeds) in semanticGroups)
        {
            reasons.Add($"semantically related to {group} (task mentions {string.Join(", ", seeds)})");
        }

        // 3. Domain match against changed files/entities.
        var domainHits = domainTokens.Where(ruleTokens.Contains).ToList();
        var domain = domainTokens.Count == 0 ? 0 : (double)domainHits.Count / domainTokens.Count;
        if (domainHits.Count > 0)
        {
            reasons.Add($"matches changed code: {string.Join(", ", domainHits)}");
        }

        // 4. Task-type alignment.
        var taskType = 0.0;
        if (TaskTypeTerms.TryGetValue(request.TaskType, out var typeTerms))
        {
            var typeHits = typeTerms.Where(ruleTokens.Contains).ToList();
            if (typeHits.Count > 0)
            {
                taskType = Math.Min(1.0, 0.5 + 0.25 * typeHits.Count);
                reasons.Add($"relevant to {request.TaskType} work: {string.Join(", ", typeHits)}");
            }
        }

        // 5. Scope: a project-specific match beats a global rule.
        double scope;
        var projectScoped = false;
        if (!string.IsNullOrWhiteSpace(request.ScopeValue)
            && rule.ScopeLevel != ScopeLevel.Global
            && string.Equals(rule.ScopeValue, request.ScopeValue, StringComparison.OrdinalIgnoreCase))
        {
            scope = 1.0;
            projectScoped = true;
            reasons.Add($"project-specific rule for {rule.ScopeValue}");
        }
        else
        {
            scope = rule.ScopeLevel == ScopeLevel.Global ? 0.2 : 0.0;
        }

        var relevance =
            KeywordWeight * keyword +
            SemanticWeight * semantic +
            DomainWeight * domain +
            TaskTypeWeight * taskType +
            ScopeWeight * scope;

        // Confidence weighting: confidence scales relevance without zeroing it,
        // and promoted rules get a small lift.
        var confidence = Math.Clamp(rule.Confidence, 0.0, 1.0);
        var confidenceFactor = 0.5 + 0.5 * confidence;
        var statusFactor = rule.Status == RuleStatus.Promoted ? 1.1 : 1.0;
        // Seed rules are dampened so learned rules of equal relevance outrank them.
        var isSeed = rule.Source == RuleSource.BuiltInSeed;
        var sourceFactor = isSeed ? SeedScoreDampening : 1.0;
        var score = relevance * confidenceFactor * statusFactor * sourceFactor;

        var highTrust = confidence >= 0.8 || rule.Status == RuleStatus.Promoted;
        if (highTrust)
        {
            reasons.Add($"high confidence ({confidence:0.00}){(rule.Status == RuleStatus.Promoted ? ", promoted" : string.Empty)}");
        }

        if (isSeed)
        {
            reasons.Add("seed rule (starter guidance)");
        }

        return new Assessment(rule, relevance, score, reasons, projectScoped, highTrust, IsProhibition(rule), false, isSeed);
    }

    /// <summary>
    /// Keeps at most <see cref="SeedInjectionCap"/> seed rules (highest-ranked first) unless
    /// the task is tidy/refactor-focused, in which case all ranked seeds are kept. Learned
    /// rules are never dropped by this cap.
    /// </summary>
    private static List<Assessment> CapSeedRules(List<Assessment> ranked, HashSet<string> taskTokens, TaskType taskType)
    {
        var tidyFocused = taskType == TaskType.Refactor || taskTokens.Overlaps(TidyTaskTerms);
        if (tidyFocused)
        {
            return ranked;
        }

        var seedsKept = 0;
        var capped = new List<Assessment>(ranked.Count);
        foreach (var a in ranked)
        {
            if (!a.IsSeed)
            {
                capped.Add(a);
                continue;
            }

            if (seedsKept < SeedInjectionCap)
            {
                capped.Add(a);
                seedsKept++;
            }
        }

        return capped;
    }

    private static ContextInjectionResult PackIntoBudget(List<Assessment> ranked, int budget, int limit, int prunedByPolicy)
    {
        var mustFollow = new List<InjectedRule>();
        var warnings = new List<InjectedRule>();
        var suggested = new List<InjectedRule>();

        // Bucket first so budgeting can prioritise must-follow and warnings.
        var bucketed = ranked
            .Select(a => (Assessment: a, Injected: ToInjected(a)))
            .ToList();

        var tokensUsed = 0;
        var trimmed = 0;
        var selected = 0;

        // Fill in priority order: must-follow, then warnings, then suggested.
        foreach (var importance in new[] { RuleImportance.MustFollow, RuleImportance.Warning, RuleImportance.Suggested })
        {
            foreach (var (_, injected) in bucketed.Where(b => b.Injected.Importance == importance))
            {
                if (selected >= limit || tokensUsed + injected.EstimatedTokens > budget)
                {
                    trimmed++;
                    continue;
                }

                tokensUsed += injected.EstimatedTokens;
                selected++;
                switch (importance)
                {
                    case RuleImportance.MustFollow: mustFollow.Add(injected); break;
                    case RuleImportance.Warning: warnings.Add(injected); break;
                    default: suggested.Add(injected); break;
                }
            }
        }

        var explanation =
            $"Selected {mustFollow.Count + warnings.Count + suggested.Count} rule(s) " +
            $"({mustFollow.Count} must-follow, {warnings.Count} warning(s), {suggested.Count} suggested) " +
            $"using {tokensUsed}/{budget} tokens.";
        if (prunedByPolicy > 0)
        {
            explanation += $" Policy engine set aside {prunedByPolicy} conflicting/superseded rule(s).";
        }

        if (trimmed > 0)
        {
            explanation += $" {trimmed} relevant rule(s) trimmed to fit the token budget.";
        }

        return new ContextInjectionResult
        {
            MustFollow = mustFollow,
            Suggested = suggested,
            Warnings = warnings,
            TokensUsed = tokensUsed,
            TokenBudget = budget,
            Explanation = explanation,
        };
    }

    private static InjectedRule ToInjected(Assessment a)
    {
        var importance = a.Prohibition
            ? RuleImportance.Warning
            // Unapproved (Pending) rules, and seed rules, are surfaced but never as
            // must-follow: seeds are starter guidance, not project truth.
            : !a.Unapproved && !a.IsSeed && (a.HighTrust || a.ProjectScoped) && a.Score >= MustFollowFloor
                ? RuleImportance.MustFollow
                : RuleImportance.Suggested;

        var explanation = a.Reasons.Count > 0
            ? $"{string.Join("; ", a.Reasons)} (score {a.Score:0.00})."
            : $"Relevant to the task (score {a.Score:0.00}).";

        return new InjectedRule
        {
            Rule = a.Rule,
            Importance = importance,
            Score = Math.Round(a.Score, 4),
            Relevance = Math.Round(a.Relevance, 4),
            Explanation = explanation,
            MatchReasons = a.Reasons,
            EstimatedTokens = EstimateTokens(a.Rule, explanation),
        };
    }

    private static int EstimateTokens(RecallRule rule, string explanation)
    {
        // ~4 characters per token, plus a little structural overhead.
        var chars = rule.RuleText.Length + rule.Mistake.Length + rule.Trigger.Length + explanation.Length;
        return (int)Math.Ceiling(chars / 4.0) + 8;
    }

    private static bool IsProhibition(RecallRule rule)
    {
        var text = rule.RuleText.ToLowerInvariant();
        return text.StartsWith("never ", StringComparison.Ordinal)
            || text.StartsWith("avoid ", StringComparison.Ordinal)
            || text.Contains("do not", StringComparison.Ordinal)
            || text.Contains("don't", StringComparison.Ordinal)
            || text.Contains("should not", StringComparison.Ordinal)
            || text.Contains("must not", StringComparison.Ordinal);
    }

    private static ContextInjectionResult Empty(int budget, string explanation) => new()
    {
        MustFollow = [],
        Suggested = [],
        Warnings = [],
        TokensUsed = 0,
        TokenBudget = budget,
        Explanation = explanation,
    };

    private sealed record Assessment(
        RecallRule Rule,
        double Relevance,
        double Score,
        List<string> Reasons,
        bool ProjectScoped,
        bool HighTrust,
        bool Prohibition,
        bool Unapproved,
        bool IsSeed);
}
