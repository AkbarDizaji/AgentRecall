using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Conflicts;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Memory;

namespace AgentRecall.Core.Lifecycle;

/// <summary>
/// Default <see cref="IRuleLifecycleRecommendationService"/>. Computes deterministic
/// per-rule metrics (retrieval count, positive/negative outcomes, days since used,
/// conflicts) and proposes one lifecycle action per rule by a fixed priority, plus
/// supersede recommendations from decisively-resolved conflicts. Suggesting only
/// reads rules and writes recommendation rows; it never mutates a rule.
/// </summary>
public sealed class RuleLifecycleRecommendationService : IRuleLifecycleRecommendationService
{
    public const int StaleDays = 180;
    public const double StaleConfidence = 0.30;
    public const double PromoteConfidence = 0.80;
    public const int FrequentRetrieval = 5;
    public const double ReviewLowConfidence = 0.40;
    public const int RepeatedNegatives = 2;

    /// <summary>
    /// Resolution-confidence above which a conflict is "clearly" won — strong enough
    /// to recommend Supersede. Below it the rules are comparable, so we recommend
    /// Review instead. Set above the small margin a recency tiebreak alone produces.
    /// </summary>
    public const double DecisiveConflict = 0.62;
    public const double ConfidenceStep = 0.10;

    private readonly IRecallRuleRepository _rules;
    private readonly IRecallEventRepository _events;
    private readonly IRuleOutcomeRepository _outcomes;
    private readonly IRuleLifecycleRecommendationRepository _recs;
    private readonly IRuleConflictDetector _conflictDetector;
    private readonly IRuleResolutionService _resolution;
    private readonly IMemoryWorthinessClassifier _classifier;
    private readonly IRuleLifecycleService _lifecycle;

    public RuleLifecycleRecommendationService(
        IRecallRuleRepository rules,
        IRecallEventRepository events,
        IRuleOutcomeRepository outcomes,
        IRuleLifecycleRecommendationRepository recs,
        IRuleConflictDetector conflictDetector,
        IRuleResolutionService resolution,
        IMemoryWorthinessClassifier classifier,
        IRuleLifecycleService lifecycle)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _outcomes = outcomes ?? throw new ArgumentNullException(nameof(outcomes));
        _recs = recs ?? throw new ArgumentNullException(nameof(recs));
        _conflictDetector = conflictDetector ?? throw new ArgumentNullException(nameof(conflictDetector));
        _resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public async Task<IReadOnlyList<RuleLifecycleRecommendation>> SuggestAsync(
        RecommendationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var allRules = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        var events = await _events.ListAsync(cancellationToken).ConfigureAwait(false);
        var outcomes = await _outcomes.ListAsync(cancellationToken).ConfigureAwait(false);
        var existing = await _recs.ListAsync(cancellationToken).ConfigureAwait(false);

        var rules = allRules.Where(r => MatchesScope(r, query)).ToList();
        var rulesById = rules.ToDictionary(r => r.Id);

        var retrieval = events
            .Where(e => e.Type == RecallEventType.RuleApplied && e.RuleId is not null)
            .GroupBy(e => e.RuleId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        var positives = outcomes.Where(o => o.ConfidenceDelta > 0).GroupBy(o => o.RuleId).ToDictionary(g => g.Key, g => g.Count());
        var negatives = outcomes.Where(o => o.ConfidenceDelta < 0).GroupBy(o => o.RuleId).ToDictionary(g => g.Key, g => g.Count());

        // Resolve conflicts among the in-force corpus.
        var inForce = rules.Where(r => !r.Deprecated && r.Status is RuleStatus.Active or RuleStatus.Promoted).ToList();
        var supersedeTarget = new Dictionary<int, RecallRule>();   // loser id -> winner
        var unresolvedConflict = new HashSet<int>();
        var anyConflict = new HashSet<int>();

        foreach (var conflict in _conflictDetector.Detect(inForce))
        {
            var members = conflict.RuleIds.Where(rulesById.ContainsKey).Select(id => rulesById[id]).ToList();
            if (members.Count < 2)
            {
                continue;
            }

            foreach (var m in members) anyConflict.Add(m.Id);

            var resolution = _resolution.Resolve(members);
            var winner = rulesById[resolution.SelectedRuleId];
            var losers = members.Where(m => m.Id != winner.Id).ToList();

            if (resolution.Confidence >= DecisiveConflict)
            {
                foreach (var loser in losers)
                {
                    if (loser.Status is RuleStatus.Active or RuleStatus.Promoted && loser.SupersededById is null)
                    {
                        supersedeTarget[loser.Id] = winner;
                    }
                }
            }
            else
            {
                foreach (var m in members) unresolvedConflict.Add(m.Id);
            }
        }

        var candidates = new List<RuleLifecycleRecommendation>();

        foreach (var rule in rules.OrderBy(r => r.Id))
        {
            var ret = retrieval.GetValueOrDefault(rule.Id);
            var pos = positives.GetValueOrDefault(rule.Id);
            var neg = negatives.GetValueOrDefault(rule.Id);
            var days = (int)(query.AsOf - (rule.LastUsedAt ?? rule.CreatedAt)).TotalDays;
            var evidence = $"Confidence {rule.Confidence:0.00}; retrieved {ret}x; +{pos}/-{neg} outcomes; conflicts {(anyConflict.Contains(rule.Id) ? 1 : 0)}; {days}d since used";

            // Supersede: this rule lost a decisive conflict.
            if (supersedeTarget.TryGetValue(rule.Id, out var winner))
            {
                candidates.Add(Make(RecommendationType.Supersede, rule.Id, winner.Id,
                    $"Superseded by stronger rule #{winner.Id}.",
                    evidence + $"; replaced by #{winner.Id}", 0.85));
                continue;
            }

            var rec = Evaluate(rule, ret, pos, neg, days, unresolvedConflict.Contains(rule.Id), anyConflict.Contains(rule.Id), inForce, evidence);
            if (rec is not null)
            {
                candidates.Add(rec);
            }
        }

        return await UpsertAsync(candidates, existing, query.Type, cancellationToken).ConfigureAwait(false);
    }

    private RuleLifecycleRecommendation? Evaluate(
        RecallRule rule, int ret, int pos, int neg, int days,
        bool unresolved, bool inConflict, IReadOnlyList<RecallRule> inForce, string evidence)
    {
        // Superseded rules should be archived (cleanup).
        if (rule.Status == RuleStatus.Superseded)
        {
            return Make(RecommendationType.Archive, rule.Id, null, "Rule is superseded; archive it.", evidence, 0.90);
        }

        if (rule.Status is not (RuleStatus.Active or RuleStatus.Promoted))
        {
            return null; // Pending/Draft/Retired/Archived are not analysed here.
        }

        // Stale and low value → archive, unless it is the only rule for a busy category.
        if (days >= StaleDays && rule.Confidence <= StaleConfidence)
        {
            var onlyOfCategory = inForce.Count(r => r.Category == rule.Category) == 1;
            if (onlyOfCategory && ret >= FrequentRetrieval)
            {
                return Make(RecommendationType.Review, rule.Id, null,
                    "Stale and low-confidence, but the only rule for a busy category — review before archiving.", evidence, 0.60);
            }

            return Make(RecommendationType.Archive, rule.Id, null,
                "Not retrieved in a long time and low confidence.", evidence, 0.80);
        }

        if (string.IsNullOrWhiteSpace(rule.Trigger) || string.IsNullOrWhiteSpace(rule.RuleText))
        {
            return Make(RecommendationType.Review, rule.Id, null, "Missing condition or action — incomplete rule.", evidence, 0.60);
        }

        if (_classifier.Classify(rule.RuleText).Category == RuleCategory.CodeFact)
        {
            return Make(RecommendationType.Review, rule.Id, null, "Reads as a code fact rather than a reusable lesson.", evidence, 0.60);
        }

        if (unresolved)
        {
            return Make(RecommendationType.Review, rule.Id, null, "Has an unresolved conflict with a comparable rule.", evidence, 0.60);
        }

        if (ret >= FrequentRetrieval && neg >= RepeatedNegatives)
        {
            return Make(RecommendationType.Review, rule.Id, null, "Frequently retrieved but with repeated negative outcomes.", evidence, 0.60);
        }

        if (rule.Confidence < ReviewLowConfidence && ret >= FrequentRetrieval)
        {
            return Make(RecommendationType.Review, rule.Id, null, "Low confidence yet frequently retrieved.", evidence, 0.60);
        }

        if (rule.Status == RuleStatus.Active && rule.Confidence >= PromoteConfidence && ret >= FrequentRetrieval && neg == 0 && !inConflict)
        {
            return Make(RecommendationType.Promote, rule.Id, null, "High confidence, frequently retrieved, no conflicts.", evidence, rule.Confidence);
        }

        if (pos >= 3 && neg == 0 && rule.Confidence is >= 0.50 and < PromoteConfidence)
        {
            return Make(RecommendationType.RaiseConfidence, rule.Id, null, "Repeated positive outcomes warrant higher confidence.", evidence, 0.70);
        }

        if (neg >= RepeatedNegatives && pos == 0 && rule.Confidence > StaleConfidence)
        {
            return Make(RecommendationType.LowerConfidence, rule.Id, null, "Repeated negative outcomes warrant lower confidence.", evidence, 0.70);
        }

        return null;
    }

    private static RuleLifecycleRecommendation Make(
        RecommendationType type, int ruleId, int? targetRuleId, string reason, string evidence, double confidence) => new()
    {
        RuleId = ruleId,
        TargetRuleId = targetRuleId,
        RecommendationType = type,
        Reason = reason,
        Evidence = evidence,
        Confidence = Math.Round(confidence, 2),
        Signature = $"{type}|{ruleId}|{(targetRuleId?.ToString() ?? "-")}",
        Status = RecommendationStatus.Suggested,
    };

    /// <summary>Upserts candidates idempotently and returns the current suggestions.</summary>
    private async Task<IReadOnlyList<RuleLifecycleRecommendation>> UpsertAsync(
        List<RuleLifecycleRecommendation> candidates,
        IReadOnlyList<RuleLifecycleRecommendation> existing,
        RecommendationType? typeFilter,
        CancellationToken cancellationToken)
    {
        var bySignature = existing
            .GroupBy(r => r.Signature, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Id).First(), StringComparer.Ordinal);

        var result = new List<RuleLifecycleRecommendation>();

        foreach (var candidate in candidates)
        {
            if (bySignature.TryGetValue(candidate.Signature, out var prior))
            {
                // A previously rejected/applied/accepted signature is not re-proposed.
                if (prior.Status != RecommendationStatus.Suggested)
                {
                    continue;
                }

                prior.Reason = candidate.Reason;
                prior.Evidence = candidate.Evidence;
                prior.Confidence = candidate.Confidence;
                prior.TargetRuleId = candidate.TargetRuleId;
                prior.UpdatedAt = DateTimeOffset.UtcNow;
                await _recs.UpdateAsync(prior, cancellationToken).ConfigureAwait(false);
                result.Add(prior);
            }
            else
            {
                result.Add(await _recs.AddAsync(candidate, cancellationToken).ConfigureAwait(false));
            }
        }

        return result
            .Where(r => typeFilter is null || r.RecommendationType == typeFilter)
            .OrderByDescending(r => r.Confidence)
            .ThenBy(r => r.RuleId)
            .ThenBy(r => r.RecommendationType)
            .ToList();
    }

    public async Task<RuleLifecycleRecommendation?> ApplyAsync(int id, CancellationToken cancellationToken = default)
    {
        var rec = await _recs.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (rec is null || rec.Status is RecommendationStatus.Applied or RecommendationStatus.Rejected)
        {
            return rec;
        }

        switch (rec.RecommendationType)
        {
            case RecommendationType.Promote:
                await _lifecycle.PromoteAsync(rec.RuleId, cancellationToken).ConfigureAwait(false);
                break;

            case RecommendationType.Archive:
                await _lifecycle.ArchiveAsync(rec.RuleId, cancellationToken).ConfigureAwait(false);
                break;

            case RecommendationType.Supersede:
                if (rec.TargetRuleId is null)
                {
                    throw new InvalidOperationException("Supersede recommendation has no target rule.");
                }

                await _lifecycle.SupersedeAsync(rec.RuleId, rec.TargetRuleId.Value, cancellationToken).ConfigureAwait(false);
                break;

            case RecommendationType.RaiseConfidence:
                await AdjustConfidenceAsync(rec.RuleId, ConfidenceStep, cancellationToken).ConfigureAwait(false);
                break;

            case RecommendationType.LowerConfidence:
                await AdjustConfidenceAsync(rec.RuleId, -ConfidenceStep, cancellationToken).ConfigureAwait(false);
                break;

            case RecommendationType.Review:
                // Review never mutates the rule; accepting just acknowledges it.
                rec.Status = RecommendationStatus.Accepted;
                rec.UpdatedAt = DateTimeOffset.UtcNow;
                await _recs.UpdateAsync(rec, cancellationToken).ConfigureAwait(false);
                return rec;
        }

        rec.Status = RecommendationStatus.Applied;
        rec.UpdatedAt = DateTimeOffset.UtcNow;
        await _recs.UpdateAsync(rec, cancellationToken).ConfigureAwait(false);
        return rec;
    }

    public async Task<RuleLifecycleRecommendation?> RejectAsync(int id, string reason, CancellationToken cancellationToken = default)
    {
        var rec = await _recs.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (rec is null)
        {
            return null;
        }

        rec.Status = RecommendationStatus.Rejected;
        rec.RejectedReason = string.IsNullOrWhiteSpace(reason) ? "Rejected." : reason.Trim();
        rec.UpdatedAt = DateTimeOffset.UtcNow;
        await _recs.UpdateAsync(rec, cancellationToken).ConfigureAwait(false);
        return rec;
    }

    private async Task AdjustConfidenceAsync(int ruleId, double delta, CancellationToken cancellationToken)
    {
        var rule = await _rules.GetAsync(ruleId, cancellationToken).ConfigureAwait(false);
        if (rule is null)
        {
            return;
        }

        rule.Confidence = Math.Round(Math.Clamp(rule.Confidence + delta, 0.0, 1.0), 2);
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        await _rules.UpdateAsync(rule, cancellationToken).ConfigureAwait(false);
    }

    private static bool MatchesScope(RecallRule rule, RecommendationQuery query)
    {
        if (query.ScopeLevel is { } level && rule.ScopeLevel != level)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(query.ScopeValue)
            || string.Equals(rule.ScopeValue, query.ScopeValue, StringComparison.OrdinalIgnoreCase);
    }
}
