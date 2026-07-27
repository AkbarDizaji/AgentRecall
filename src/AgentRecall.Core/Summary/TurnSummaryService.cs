using System.Globalization;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Finalization;

namespace AgentRecall.Core.Summary;

/// <summary>
/// Default <see cref="ITurnSummaryService"/>. It joins the retrieval activity recorded at
/// UserPromptSubmit with the capture activity recorded at Stop/finalize-turn — preferring
/// the turn correlation id, and falling back to a conservative time window only when no id
/// is present — and reads the structured <see cref="TurnFinalization"/> for the
/// captured / suggested / skipped decision. Nothing here is parsed from rendered notices.
/// </summary>
public sealed class TurnSummaryService : ITurnSummaryService
{
    /// <summary>How many recent activity rows to scan when assembling one turn.</summary>
    private const int ActivityScanLimit = 500;

    /// <summary>Short title length, matching the activity factory's labels.</summary>
    private const int TitleLength = 60;

    /// <summary>
    /// Conservative window used only when no turn id is available, so a summary never folds
    /// in unrelated older activity. Documented fallback for the "timestamps only" case.
    /// </summary>
    private static readonly TimeSpan FallbackWindow = TimeSpan.FromMinutes(2);

    private readonly IAgentRecallActivityRepository _activities;
    private readonly ITurnFinalizationRepository _finalizations;
    private readonly IRecallRuleRepository _rules;
    private readonly ICareerImpactCandidateRepository _careerImpact;
    private readonly IDocOpportunityCandidateRepository _docOpportunity;

    public TurnSummaryService(
        IAgentRecallActivityRepository activities,
        ITurnFinalizationRepository finalizations,
        IRecallRuleRepository rules,
        ICareerImpactCandidateRepository careerImpact,
        IDocOpportunityCandidateRepository docOpportunity)
    {
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
        _finalizations = finalizations ?? throw new ArgumentNullException(nameof(finalizations));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _careerImpact = careerImpact ?? throw new ArgumentNullException(nameof(careerImpact));
        _docOpportunity = docOpportunity ?? throw new ArgumentNullException(nameof(docOpportunity));
    }

    public async Task<TurnSummary> BuildForTurnAsync(string? turnId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(turnId))
        {
            return await BuildLastAsync(cancellationToken).ConfigureAwait(false);
        }

        var recent = await _activities.ListRecentAsync(ActivityScanLimit, cancellationToken).ConfigureAwait(false);
        var turnActivities = recent
            .Where(a => string.Equals(a.TurnId, turnId, StringComparison.Ordinal))
            .ToList();

        var finalization = await FindFinalizationByTurnAsync(turnId, cancellationToken).ConfigureAwait(false);
        return await BuildAsync(turnId, turnActivities, finalization, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TurnSummary> BuildLastAsync(CancellationToken cancellationToken = default)
    {
        var recent = await _activities.ListRecentAsync(ActivityScanLimit, cancellationToken).ConfigureAwait(false);

        // Preferred: the most recent activity that carries a turn id anchors the summary.
        var anchor = recent.FirstOrDefault(a => !string.IsNullOrEmpty(a.TurnId));
        if (anchor is not null)
        {
            return await BuildForTurnAsync(anchor.TurnId, cancellationToken).ConfigureAwait(false);
        }

        // Fallback: no turn id anywhere. Anchor on the most recent activity (or the most
        // recent finalization when there is no activity) and take a short window around it,
        // so an old, unrelated turn is never folded in.
        var latestFinal = await LatestFinalizationAsync(cancellationToken).ConfigureAwait(false);
        if (recent.Count == 0 && latestFinal is null)
        {
            return new TurnSummary();
        }

        var anchorTime = recent.Count > 0 ? recent[0].CreatedAt : latestFinal!.CreatedAt;
        var windowStart = anchorTime - FallbackWindow;

        var windowActivities = recent
            .Where(a => string.IsNullOrEmpty(a.TurnId) && a.CreatedAt >= windowStart)
            .ToList();

        var finalization = latestFinal is not null && latestFinal.CreatedAt >= windowStart ? latestFinal : null;
        return await BuildAsync(turnId: null, windowActivities, finalization, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TurnSummary> BuildAsync(
        string? turnId,
        IReadOnlyList<AgentRecallActivity> activities,
        TurnFinalization? finalization,
        CancellationToken cancellationToken)
    {
        var cache = new Dictionary<int, RecallRule?>();

        var usedIds = OrderedRuleIds(activities, ActivityType.ContextFetched);
        var rememberedIds = OrderedRuleIds(activities, ActivityType.SuggestionRemembered);
        var ignoredIds = OrderedRuleIds(activities, ActivityType.SuggestionIgnored);

        // Captured / suggested / skipped / errors come from the structured finalization
        // record when present (the canonical Stop-hook path), and additionally from any
        // feedback-path activities (the older capture hook), deduped by id.
        var capturedIds = new List<int>();
        var suggestedIds = new List<int>();
        var skips = new List<TurnSummarySkip>();
        var errors = new List<string>();

        if (finalization is not null)
        {
            capturedIds.AddRange(ParseIds(finalization.CapturedRuleIds));
            suggestedIds.AddRange(ParseIds(finalization.SuggestedRuleIds));
            skips.AddRange(SplitLines(finalization.SkippedReasons).Select(r => new TurnSummarySkip { Reason = r }));
            errors.AddRange(SplitParts(finalization.ErrorSummary, ';'));
        }

        foreach (var activity in activities)
        {
            switch (activity.ActivityType)
            {
                case ActivityType.RuleCaptured:
                    capturedIds.AddRange(ParseIds(activity.RuleIds));
                    break;
                case ActivityType.RuleSuggested:
                    suggestedIds.AddRange(ParseIds(activity.RuleIds));
                    break;
                case ActivityType.CandidateSkipped:
                    skips.Add(new TurnSummarySkip { Reason = SkipReason(activity) });
                    break;
            }
        }

        var captured = new List<TurnSummaryRule>();
        foreach (var id in Distinct(capturedIds))
        {
            captured.Add(await ToRuleAsync(id, withReason: true, cache, cancellationToken).ConfigureAwait(false));
        }

        var suggested = new List<TurnSummaryRule>();
        foreach (var id in Distinct(suggestedIds))
        {
            suggested.Add(await ToRuleAsync(id, withReason: false, cache, cancellationToken).ConfigureAwait(false));
        }

        var used = new List<TurnSummaryRule>();
        foreach (var id in usedIds)
        {
            used.Add(await ToRuleAsync(id, withReason: false, cache, cancellationToken).ConfigureAwait(false));
        }

        var remembered = new List<TurnSummaryRule>();
        foreach (var id in rememberedIds)
        {
            remembered.Add(await ToRuleAsync(id, withReason: false, cache, cancellationToken).ConfigureAwait(false));
        }

        var ignored = new List<TurnSummaryRule>();
        foreach (var id in ignoredIds)
        {
            ignored.Add(await ToRuleAsync(id, withReason: false, cache, cancellationToken).ConfigureAwait(false));
        }

        // A short, model-safe pointer only — never the full career summary. Present only
        // when the optional detector flagged significant work for this turn.
        string? careerPointer = null;
        if (!string.IsNullOrEmpty(turnId))
        {
            var candidate = await _careerImpact.FindByTurnAsync(turnId, cancellationToken).ConfigureAwait(false);
            if (candidate is { IsSignificant: true })
            {
                var hint = SplitLines(candidate.Reasons).FirstOrDefault();
                careerPointer = CareerImpact.CareerImpactRenderer.BuildTurnSummaryPointer(hint);
            }
        }

        // A short, model-safe pointer only — never the reason or key points. Present only when
        // the host-supplied judge offered a document for this turn and it is still Open (once
        // written, the pointer would be stale — `document status` is the source of truth then).
        string? docPointer = null;
        if (!string.IsNullOrEmpty(turnId))
        {
            var docCandidate = await _docOpportunity.FindByTurnAsync(turnId, cancellationToken).ConfigureAwait(false);
            if (docCandidate is { Status: DocOpportunityStatus.Open })
            {
                docPointer = DocOpportunity.DocOpportunityRenderer.BuildTurnSummaryPointer(
                    docCandidate.DocumentType, docCandidate.SuggestedTitle, docCandidate.Confidence);
            }
        }

        return new TurnSummary
        {
            TurnId = turnId,
            Used = used,
            Captured = captured,
            Suggested = suggested,
            Skipped = DedupSkips(skips),
            Remembered = remembered,
            Ignored = ignored,
            Errors = errors,
            CareerImpact = careerPointer,
            DocOpportunity = docPointer,
        };
    }

    private async Task<TurnSummaryRule> ToRuleAsync(
        int id,
        bool withReason,
        Dictionary<int, RecallRule?> cache,
        CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(id, out var rule))
        {
            rule = await _rules.GetAsync(id, cancellationToken).ConfigureAwait(false);
            cache[id] = rule;
        }

        if (rule is null)
        {
            return new TurnSummaryRule { Id = id, Title = $"rule #{id}" };
        }

        var category = rule.Category != RuleCategory.Unknown ? rule.Category.ToString() : null;
        var reason = withReason && rule.CaptureReason != Capture.CaptureReason.None
            ? rule.CaptureReason.ToString()
            : null;

        return new TurnSummaryRule
        {
            Id = id,
            Title = ShortTitle(rule),
            Category = category,
            Reason = reason,
            Seed = rule.Source == RuleSource.BuiltInSeed,
            Standing = rule.AlwaysApply,
        };
    }

    private async Task<TurnFinalization?> FindFinalizationByTurnAsync(string turnId, CancellationToken cancellationToken)
    {
        var all = await _finalizations.ListAsync(cancellationToken).ConfigureAwait(false);

        // As in TurnFinalizer.GetLastAsync: a real judged decision for this turn must win
        // over a later "judge unavailable" record from the native Stop hook firing with no
        // supplied judgment.
        return all
            .Where(f => string.Equals(f.TurnId, turnId, StringComparison.Ordinal))
            .OrderByDescending(f => f.DecisionSource == TurnFinalizer.JudgeDecisionSource)
            .ThenByDescending(f => f.CreatedAt)
            .ThenByDescending(f => f.Id)
            .FirstOrDefault();
    }

    private async Task<TurnFinalization?> LatestFinalizationAsync(CancellationToken cancellationToken)
    {
        var all = await _finalizations.ListAsync(cancellationToken).ConfigureAwait(false);
        return all
            .OrderByDescending(f => f.CreatedAt)
            .ThenByDescending(f => f.Id)
            .FirstOrDefault();
    }

    private static IReadOnlyList<int> OrderedRuleIds(IReadOnlyList<AgentRecallActivity> activities, ActivityType type)
    {
        var ids = new List<int>();
        foreach (var activity in activities.Where(a => a.ActivityType == type))
        {
            ids.AddRange(ParseIds(activity.RuleIds));
        }

        return Distinct(ids);
    }

    // The same skip can arrive from both the finalization record and a CandidateSkipped
    // activity, worded slightly differently ("Assistant prose, …" vs "assistant prose, …").
    // Collapse them by a normalized key so the summary shows each skip once.
    private static IReadOnlyList<TurnSummarySkip> DedupSkips(IEnumerable<TurnSummarySkip> skips)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<TurnSummarySkip>();
        foreach (var skip in skips)
        {
            var key = (skip.Reason ?? string.Empty).Trim().TrimEnd('.', '!', '?').ToLowerInvariant();
            if (seen.Add(key))
            {
                ordered.Add(skip);
            }
        }

        return ordered;
    }

    private static string SkipReason(AgentRecallActivity activity)
    {
        // Prefer the stored detail (the structured reason) over the headline summary.
        var detail = SplitLines(activity.Details).FirstOrDefault();
        return string.IsNullOrWhiteSpace(detail) ? activity.Summary : detail;
    }

    private static string ShortTitle(RecallRule rule)
    {
        var label = !string.IsNullOrWhiteSpace(rule.Trigger) ? rule.Trigger : rule.RuleText;
        label = (label ?? string.Empty).Trim();
        return label.Length <= TitleLength ? label : label[..(TitleLength - 1)] + "…";
    }

    private static List<int> Distinct(IEnumerable<int> ids)
    {
        var seen = new HashSet<int>();
        var ordered = new List<int>();
        foreach (var id in ids)
        {
            if (seen.Add(id))
            {
                ordered.Add(id);
            }
        }

        return ordered;
    }

    private static IEnumerable<int> ParseIds(string? csv) =>
        SplitParts(csv, ',')
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (int?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value);

    private static IReadOnlyList<string> SplitLines(string? value) =>
        SplitParts(value, '\n');

    private static IReadOnlyList<string> SplitParts(string? value, char separator) =>
        string.IsNullOrEmpty(value)
            ? []
            : value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
