using System.Globalization;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Seeds;

/// <summary>
/// Deterministic passive reinforcement for seed rules: when a seed rule is retrieved and
/// no correction or rejection follows, its confidence rises slowly toward a conservative
/// ceiling. Explicit positive/negative feedback still flows through the normal outcome and
/// lifecycle systems (a seed rule is a normal rule after install); this service only adds
/// the small, capped "used repeatedly without complaint" nudge, and never on its own pushes
/// a seed rule to high confidence.
/// </summary>
public sealed class SeedConfidenceService : ISeedConfidenceService
{
    /// <summary>Confidence added per uneventful retrieval.</summary>
    public const double PassiveStep = 0.02;

    /// <summary>Passive reinforcement never raises confidence above this.</summary>
    public const double PassiveCeiling = 0.80;

    /// <summary>Marker written to the event ledger so credited uses are never counted twice.</summary>
    internal const string PassiveMarker = "seed-passive-credit";

    private readonly IRecallRuleRepository _rules;
    private readonly IRecallEventRepository _events;
    private readonly IRuleOutcomeRepository _outcomes;

    public SeedConfidenceService(
        IRecallRuleRepository rules,
        IRecallEventRepository events,
        IRuleOutcomeRepository outcomes)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _outcomes = outcomes ?? throw new ArgumentNullException(nameof(outcomes));
    }

    public async Task<SeedReinforcementResult> ReinforceAsync(CancellationToken cancellationToken = default)
    {
        var all = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        var seeds = all
            .Where(r => r.Source == RuleSource.BuiltInSeed && r.Status != RuleStatus.Archived && !r.Deprecated)
            .ToList();

        if (seeds.Count == 0)
        {
            return new SeedReinforcementResult { Adjustments = [] };
        }

        var events = await _events.ListAsync(cancellationToken).ConfigureAwait(false);
        var outcomes = await _outcomes.ListAsync(cancellationToken).ConfigureAwait(false);

        var adjustments = new List<SeedConfidenceAdjustment>();
        var updated = new List<RecallRule>();
        var markers = new List<RecallEvent>();

        foreach (var rule in seeds)
        {
            // A correction or rejection means this rule's guidance did not hold — no passive credit.
            var negatives = outcomes.Count(o => o.RuleId == rule.Id && o.ConfidenceDelta < 0);
            if (negatives > 0)
            {
                continue;
            }

            var uses = events.Count(e => e.RuleId == rule.Id && e.Type == RecallEventType.RuleApplied);
            var alreadyCredited = events
                .Where(e => e.RuleId == rule.Id && e.Type == RecallEventType.RuleUpdated && e.Trigger == PassiveMarker)
                .Sum(e => ParseCount(e.Details));

            var newUses = uses - alreadyCredited;
            if (newUses <= 0)
            {
                continue;
            }

            var previous = rule.Confidence;
            var room = Math.Max(0.0, PassiveCeiling - previous);
            var addable = Math.Min(newUses * PassiveStep, room);
            var next = Math.Round(previous + addable, 2);

            // Record every newly observed use as credited, even when the ceiling caps the
            // confidence gain, so re-running never double-counts or churns.
            markers.Add(new RecallEvent
            {
                Type = RecallEventType.RuleUpdated,
                RuleId = rule.Id,
                Trigger = PassiveMarker,
                Details = newUses.ToString(CultureInfo.InvariantCulture),
            });

            if (next > previous)
            {
                rule.Confidence = next;
                rule.UpdatedAt = DateTimeOffset.UtcNow;
                updated.Add(rule);
                adjustments.Add(new SeedConfidenceAdjustment
                {
                    RuleId = rule.Id,
                    Title = rule.Trigger,
                    PreviousConfidence = previous,
                    NewConfidence = next,
                    UneventfulUses = newUses,
                });
            }
        }

        if (markers.Count > 0)
        {
            await _events.AddRangeAsync(markers, cancellationToken).ConfigureAwait(false);
        }

        if (updated.Count > 0)
        {
            await _rules.UpdateRangeAsync(updated, cancellationToken).ConfigureAwait(false);
        }

        return new SeedReinforcementResult { Adjustments = adjustments };
    }

    private static int ParseCount(string? details) =>
        int.TryParse(details, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
