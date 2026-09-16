using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Policy;
using AgentRecall.Core.Text;

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

    /// <summary>
    /// How often a rule this chat has already read is restated in full rather than reminded. A
    /// long chat gets compacted, so what was read early may no longer be in view; restating on
    /// every Nth injection keeps a rule recoverable without paying for it every turn.
    /// </summary>
    private const int FullRestateEvery = 6;

    /// <summary>
    /// Tokens the block spends before any rule: the heading with its contract stamp, the section
    /// labels, and the retrieval id line. Charged against the budget up front so a full block
    /// lands inside the number the caller asked for rather than a little over it — capped at a
    /// quarter of the budget, so a small budget still admits the rule it was asked for.
    /// </summary>
    private const int BlockScaffoldingTokens = 60;

    /// <summary>Score at or above which a high-trust rule is "must-follow".</summary>
    private const double MustFollowFloor = 0.15;

    /// <summary>
    /// Most always-apply (universal-constraint) rules injected per prompt. This band bypasses the
    /// relevance floor so a standing rule reaches the model on turns it shares no keywords with;
    /// the cap keeps it small and salient so it never degrades into an ignored wall of guidance.
    /// </summary>
    private const int AlwaysApplyCap = 5;

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
    private readonly Abstractions.IRuleOutcomeRepository _outcomes;

    public ContextInjectionService(
        IRecallRuleRepository rules,
        IRecallEventRepository events,
        IRetrievalRecordRepository retrievals,
        IPolicyEngine policy,
        IConceptExpander concepts,
        Conflicts.IRuleConflictDetector conflictDetector,
        Conflicts.IRuleResolutionService resolution,
        Abstractions.IRuleOutcomeRepository outcomes)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _retrievals = retrievals ?? throw new ArgumentNullException(nameof(retrievals));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _concepts = concepts ?? throw new ArgumentNullException(nameof(concepts));
        _conflictDetector = conflictDetector ?? throw new ArgumentNullException(nameof(conflictDetector));
        _resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
        _outcomes = outcomes ?? throw new ArgumentNullException(nameof(outcomes));
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

        // How each rule has actually fared, read once for the whole ranking pass. A rule injected
        // again and again without ever helping is dampened by its own record rather than waiting
        // for confidence to drift down.
        var effectiveness = await EffectivenessByRuleAsync(cancellationToken).ConfigureAwait(false);

        // What this chat has already been shown. Re-sending a rule the agent read three turns ago
        // costs its full price for nothing; a one-line reminder keeps it in view for a fraction.
        var alreadySeen = await SeenInSessionAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        // Excluded rules are dropped from the candidate pool up front, so they are neither
        // ranked/injected nor recorded as used — the single point that de-duplicates a rule
        // across repeated retrievals within one turn (see ContextRequest.ExcludeRuleIds).
        var pool = all
            .Where(RuleStatusSets.IsEffective)
            .Where(r => !request.ExcludeRuleIds.Contains(r.Id))
            .ToList();

        // Score every rule; keep those clearing the relevance floor. Always-apply rules are a
        // separate universal-constraint band: they are kept even below the floor so they reach
        // the model on turns that share no keywords with them (a style/quality rule matches
        // every task via no specific keyword). The band is capped so it never floods the context.
        var assessments = new Dictionary<int, Assessment>();
        var alwaysApply = new List<Assessment>();
        foreach (var rule in pool)
        {
            var assessment = Assess(rule, taskTokens, domainTokens, concepts, request, effectiveness);
            if (assessment.AlwaysApply)
            {
                alwaysApply.Add(assessment);
            }
            else if (assessment.Relevance >= RelevanceFloor)
            {
                assessments[rule.Id] = assessment;
            }
        }

        // Keep the highest-trust always-apply rules as the standing band (capped). Any beyond the
        // cap fall back to ordinary relevance gating, so the band stays small and the rest do not
        // silently vanish — they compete on relevance like everything else.
        var rankedAlwaysApply = alwaysApply
            .OrderByDescending(a => a.Rule.Confidence)
            .ThenByDescending(a => a.Score)
            .ThenBy(a => a.Rule.Id)
            .ToList();
        foreach (var a in rankedAlwaysApply.Take(AlwaysApplyCap))
        {
            assessments[a.Rule.Id] = a;
        }

        foreach (var a in rankedAlwaysApply.Skip(AlwaysApplyCap))
        {
            if (a.Relevance >= RelevanceFloor)
            {
                assessments[a.Rule.Id] = a with { AlwaysApply = false };
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
            foreach (var rule in all.Where(r => r.Status == RuleStatus.Pending && !r.Deprecated && !request.ExcludeRuleIds.Contains(r.Id)))
            {
                var assessment = Assess(rule, taskTokens, domainTokens, concepts, request, effectiveness) with { Unapproved = true };
                if (assessment.Relevance >= RelevanceFloor)
                {
                    ranked.Add(assessment);
                }
            }
        }

        ranked = ranked
            // Standing (always-apply) rules rank first so they claim their reserved slots before
            // relevance-ranked rules compete for the remaining token budget.
            .OrderByDescending(a => a.AlwaysApply)
            .ThenByDescending(a => a.Score)
            .ThenByDescending(a => a.Rule.Confidence)
            .ThenBy(a => a.Rule.Id)
            .ToList();

        // One lesson, said once. Two rules can carry the same action under different triggers —
        // the same guidance rendered twice, paid for twice, from a duplicate nobody noticed while
        // capturing it. The better-ranked one is kept and the other is dropped from this retrieval
        // entirely, so it is not recorded as injected either and no outcome can be claimed for it.
        ranked = CollapseDuplicateActions(ranked);

        // Cap seed rules so starter guidance never floods the context — unless the task is
        // itself about tidying/refactoring, where seed rules are the point.
        ranked = CapSeedRules(ranked, taskTokens, request.TaskType);

        // Cap Pending rules so unreviewed suggestions never flood the context.
        ranked = CapPendingRules(ranked, request.PendingCap);

        // Reserve the block's furniture from the budget, but never more than a quarter of it: a
        // caller who asks for a small budget wants the top rule, not an empty block.
        var scaffolding = Math.Min(BlockScaffoldingTokens, request.TokenBudget / 4);

        var result = PackIntoBudget(
            ranked,
            Math.Max(0, request.TokenBudget - scaffolding),
            request.Limit,
            prunedByPolicy,
            alreadySeen,
            out var trimmed);
        var beforeConflictResolution = result.All.Count();

        // Resolve conflicts among the rules that survived to injection. This catches
        // competing guidance the polarity-based policy does not (e.g. unit vs
        // integration tests): the loser is dropped and the conflict is recorded, so
        // the agent sees a single chosen rule plus an explanation.
        result = ResolveInjectedConflicts(result);

        // Conflict resolution can drop rules that PackIntoBudget already counted into
        // TokensUsed/Explanation — recompute both from what actually survived, so the
        // explanation (surfaced verbatim by the inject_context MCP tool) never overstates
        // what was injected.
        var conflictsPruned = beforeConflictResolution - result.All.Count();
        if (conflictsPruned > 0)
        {
            result = result with
            {
                TokensUsed = result.All.Sum(r => r.EstimatedTokens),
                Explanation = BuildExplanation(
                    result.MustFollow.Count, result.Warnings.Count, result.Suggested.Count,
                    result.All.Sum(r => r.EstimatedTokens), request.TokenBudget,
                    prunedByPolicy, trimmed, conflictsPruned),
            };
        }

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
            RuleIds = IdList.Join(retrieved.Select(r => r.Id)),
            SessionId = request.SessionId ?? string.Empty,
        }, cancellationToken).ConfigureAwait(false);

        return retrievalId;
    }

    /// <summary>
    /// The retrieval dampener for every rule that carries reported outcomes, keyed by rule id.
    /// Rules with too thin a record are simply absent and score unchanged.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, double>> EffectivenessByRuleAsync(CancellationToken cancellationToken)
    {
        var outcomes = await _outcomes.ListAsync(cancellationToken).ConfigureAwait(false);

        return outcomes
            .GroupBy(o => o.RuleId)
            .ToDictionary(
                group => group.Key,
                group => Outcomes.RuleEffectiveness.Factor(
                    group.Count(o => o.Type == OutcomeType.UserAccepted),
                    group.Count(o => o.Type == OutcomeType.RuleIgnored)));
    }

    private Assessment Assess(
        RecallRule rule,
        HashSet<string> taskTokens,
        HashSet<string> domainTokens,
        ConceptContext concepts,
        ContextRequest request,
        IReadOnlyDictionary<int, double> effectiveness)
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

        // 2. Semantic match via activated concept groups (no shared words needed). A rule token
        // can relate to more than one activated group at once (e.g. a term shared across
        // domains) — every relation counts, not just the first one found.
        var semanticGroups = new Dictionary<string, IReadOnlyCollection<string>>();
        var semanticHits = 0;
        foreach (var token in ruleTokens)
        {
            if (taskTokens.Contains(token) || domainTokens.Contains(token))
            {
                continue;
            }

            foreach (var (group, viaSeeds) in concepts.RelateAll(token))
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

        // 5. Scope: a rule bound to this work's project, directory, or file beats a global rule.
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
        else if (FileScopeContainsAnyChangedFile(rule, request.FileNames))
        {
            // A File/Directory-scoped rule whose scope contains a file this task touches is as
            // specific as a repository match. Without this, such a rule scores 0.0 — below a
            // global rule — whenever the request's ScopeValue is the repository (as the hooks
            // send it), so recall keyed on a file could never reward a rule bound to that file.
            scope = 1.0;
            projectScoped = true;
            reasons.Add($"rule scoped to {rule.ScopeValue}, which contains a changed file");
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
        // Dampened by its own record: injected repeatedly without ever being accepted.
        var effectivenessFactor = effectiveness.TryGetValue(rule.Id, out var measured) ? measured : 1.0;
        var score = relevance * confidenceFactor * statusFactor * sourceFactor * effectivenessFactor;

        if (effectivenessFactor < 1.0)
        {
            reasons.Add($"dampened by reported outcomes ({effectivenessFactor:0.00})");
        }

        var highTrust = confidence >= 0.8 || rule.Status == RuleStatus.Promoted;
        if (highTrust)
        {
            reasons.Add($"high confidence ({confidence:0.00}){(rule.Status == RuleStatus.Promoted ? ", promoted" : string.Empty)}");
        }

        if (isSeed)
        {
            reasons.Add("seed rule (starter guidance)");
        }

        return new Assessment(rule, relevance, score, reasons, projectScoped, highTrust, IsProhibition(rule), false, isSeed, rule.AlwaysApply);
    }

    /// <summary>
    /// True when <paramref name="rule"/> is File- or Directory-scoped and one of the task's
    /// changed files lies within that scope. This lets recall keyed on a file (e.g. the
    /// PreToolUse hook) reward a rule bound to that file or its directory — which the plain
    /// ScopeValue-equality check cannot, because a request built from a file write carries
    /// the repository as its ScopeValue, not the file path. Comparison normalises separators
    /// and matches on a segment boundary from either end, so a rule stored with a
    /// repository-relative path still matches an absolute path supplied by the host.
    /// </summary>
    private static bool FileScopeContainsAnyChangedFile(RecallRule rule, IReadOnlyList<string> fileNames)
    {
        if (fileNames.Count == 0
            || string.IsNullOrWhiteSpace(rule.ScopeValue)
            || rule.ScopeLevel is not (ScopeLevel.File or ScopeLevel.Directory))
        {
            return false;
        }

        var scopePath = NormalizePath(rule.ScopeValue);
        if (scopePath.Length == 0)
        {
            return false;
        }

        foreach (var file in fileNames)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            var filePath = NormalizePath(file);
            var matched = rule.ScopeLevel == ScopeLevel.File
                ? PathTailMatches(filePath, scopePath)
                : IsWithinDirectory(filePath, scopePath);
            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().TrimEnd('/');

    // A file matches a File-scoped rule when the paths are equal, or one is a tail of the
    // other on a segment boundary (tolerating absolute-vs-relative storage).
    private static bool PathTailMatches(string filePath, string scopePath) =>
        string.Equals(filePath, scopePath, StringComparison.OrdinalIgnoreCase)
        || filePath.EndsWith("/" + scopePath, StringComparison.OrdinalIgnoreCase)
        || scopePath.EndsWith("/" + filePath, StringComparison.OrdinalIgnoreCase);

    // A file is within a Directory-scoped rule when the directory is a segment-aligned prefix
    // of the file path, matched from either end so a relative dir matches an absolute file.
    private static bool IsWithinDirectory(string filePath, string dirPath) =>
        filePath.StartsWith(dirPath + "/", StringComparison.OrdinalIgnoreCase)
        || filePath.Contains("/" + dirPath + "/", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Keeps at most <paramref name="cap"/> Pending (Unapproved) rules — the
    /// freshest, highest-scoring ones — so an unreviewed suggestion can resurface
    /// for reinforcement without flooding the context. Null cap (the explicit
    /// `include_pending` API path) leaves every relevance-qualifying Pending rule
    /// in place; ties break by rule id descending (freshest first), since the goal
    /// is surfacing the current turn's newest unresolved suggestion.
    /// </summary>
    private static List<Assessment> CapPendingRules(List<Assessment> ranked, int? cap)
    {
        if (cap is null)
        {
            return ranked;
        }

        var keep = ranked
            .Where(a => a.Unapproved)
            .OrderByDescending(a => a.Score)
            .ThenByDescending(a => a.Rule.Confidence)
            .ThenByDescending(a => a.Rule.Id)
            .Take(cap.Value)
            .Select(a => a.Rule.Id)
            .ToHashSet();

        return ranked.Where(a => !a.Unapproved || keep.Contains(a.Rule.Id)).ToList();
    }

    private static ContextInjectionResult PackIntoBudget(
        List<Assessment> ranked,
        int budget,
        int limit,
        int prunedByPolicy,
        IReadOnlyDictionary<int, int> seen,
        out int trimmed)
    {
        var mustFollow = new List<InjectedRule>();
        var warnings = new List<InjectedRule>();
        var suggested = new List<InjectedRule>();

        // Bucket first so budgeting can prioritise must-follow and warnings.
        var bucketed = ranked
            .Select(a => (Assessment: a, Injected: ToInjected(a, seen)))
            .ToList();

        var tokensUsed = 0;
        trimmed = 0;
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

        var explanation = BuildExplanation(
            mustFollow.Count, warnings.Count, suggested.Count, tokensUsed, budget, prunedByPolicy, trimmed, conflictsPruned: 0);

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

    /// <summary>
    /// Builds the human/model-facing summary line. Called once by <see cref="PackIntoBudget"/>
    /// and again after conflict resolution (with the post-resolution counts/tokens) so the
    /// explanation never describes rules that were subsequently dropped as a conflict loser.
    /// </summary>
    private static string BuildExplanation(
        int mustFollowCount, int warningsCount, int suggestedCount, int tokensUsed, int budget,
        int prunedByPolicy, int trimmed, int conflictsPruned)
    {
        var explanation =
            $"Selected {mustFollowCount + warningsCount + suggestedCount} rule(s) " +
            $"({mustFollowCount} must-follow, {warningsCount} warning(s), {suggestedCount} suggested) " +
            $"using {tokensUsed}/{budget} tokens.";
        if (prunedByPolicy > 0)
        {
            explanation += $" Policy engine set aside {prunedByPolicy} conflicting/superseded rule(s).";
        }

        if (trimmed > 0)
        {
            explanation += $" {trimmed} relevant rule(s) trimmed to fit the token budget.";
        }

        if (conflictsPruned > 0)
        {
            explanation += $" {conflictsPruned} rule(s) set aside after resolving a conflict among injected rules.";
        }

        return explanation;
    }

    private static InjectedRule ToInjected(Assessment a, IReadOnlyDictionary<int, int> seen)
    {
        var importance = a.Prohibition
            ? RuleImportance.Warning
            // A standing (always-apply) rule is must-follow regardless of relevance score — it is a
            // universal constraint, not a task match. Unapproved (Pending) rules and seed rules are
            // surfaced but never as must-follow: seeds are starter guidance, not project truth.
            : !a.Unapproved && !a.IsSeed && (a.AlwaysApply || a.HighTrust || a.ProjectScoped) && (a.AlwaysApply || a.Score >= MustFollowFloor)
                ? RuleImportance.MustFollow
                : RuleImportance.Suggested;

        var reasons = a.AlwaysApply
            ? a.Reasons.Prepend("standing rule — applies to every task").ToList()
            : a.Reasons;
        var explanation = reasons.Count > 0
            ? $"{string.Join("; ", reasons)} (score {a.Score:0.00})."
            : $"Relevant to the task (score {a.Score:0.00}).";

        return new InjectedRule
        {
            Rule = a.Rule,
            Importance = importance,
            Score = Math.Round(a.Score, 4),
            Relevance = Math.Round(a.Relevance, 4),
            Explanation = explanation,
            MatchReasons = a.Reasons,
            EstimatedTokens = EstimateTokens(a.Rule, DetailFor(importance, a.Rule.Id, seen)),
            Detail = DetailFor(importance, a.Rule.Id, seen),
        };
    }

    /// <summary>
    /// How many times each rule has already been injected in this chat. Empty when the caller
    /// gave no session: with no shared history, every rule is new.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, int>> SeenInSessionAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new Dictionary<int, int>();
        }

        var records = await _retrievals.ListAsync(cancellationToken).ConfigureAwait(false);
        var counts = new Dictionary<int, int>();

        foreach (var record in records.Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal)))
        {
            foreach (var id in IdList.Parse(record.RuleIds))
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// The detail a rule is rendered at: full or compact the first time this chat sees it and every
    /// <see cref="FullRestateEvery"/> injections after, a one-line reminder in between.
    /// </summary>
    private static RuleDetail DetailFor(RuleImportance importance, int ruleId, IReadOnlyDictionary<int, int> seen)
    {
        var priorInjections = seen.GetValueOrDefault(ruleId);
        if (priorInjections > 0 && priorInjections % FullRestateEvery != 0)
        {
            return RuleDetail.Reminder;
        }

        return importance == RuleImportance.Suggested ? RuleDetail.Compact : RuleDetail.Full;
    }

    /// <summary>
    /// Drops rules whose action duplicates one already ranked above them. Compared on normalized
    /// text, so a re-punctuated copy still counts as the same lesson.
    /// </summary>
    private static List<Assessment> CollapseDuplicateActions(List<Assessment> ranked)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<Assessment>(ranked.Count);

        foreach (var assessment in ranked)
        {
            var action = TextNormalization.Collapse(assessment.Rule.RuleText);
            if (action.Length == 0 || seen.Add(action))
            {
                kept.Add(assessment);
            }
        }

        return kept;
    }

    /// <summary>
    /// What this rule will actually cost, measured on the text that gets injected.
    ///
    /// The estimate used to sum the stored fields plus the match explanation — but the explanation
    /// is never rendered, the rationale always is, and the labels and bullet are not free. Two code
    /// paths, drifting quietly: measured on a real block the render came to 1.5x the estimate, so a
    /// budget of 1500 could emit well past 2000. Rendering the rule is cheap and leaves nothing to
    /// drift, and the detail level is part of the cost because a suggestion is rendered compactly.
    /// </summary>
    private static int EstimateTokens(RecallRule rule, RuleDetail detail)
    {
        var rendered = ConditionalRuleFormatter.Format(rule, indent: 2, includeSource: true, detail: detail);

        // ~4 characters per token, plus the bullet and newline the section adds around it.
        return (int)Math.Ceiling(rendered.Length / 4.0) + 2;
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
        bool IsSeed,
        bool AlwaysApply);
}
