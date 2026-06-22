using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Outcomes;

/// <summary>
/// Default <see cref="IOutcomeTrackingService"/>. Resolves the target rules (one
/// rule, or every rule a retrieval injected), applies the configured confidence
/// delta clamped to [0, 1], and records an outcome row per adjustment. Duplicate
/// outcomes for the same retrieval + type + rule are suppressed so confidence
/// cannot run away. Every adjustment is durably recorded as a
/// <see cref="RuleOutcome"/> row carrying its reason — that ledger is the log.
/// </summary>
public sealed class OutcomeTrackingService : IOutcomeTrackingService
{
    private readonly IRecallRuleRepository _rules;
    private readonly IRuleOutcomeRepository _outcomes;
    private readonly IRetrievalRecordRepository _retrievals;
    private readonly AgentRecallOptions _options;

    public OutcomeTrackingService(
        IRecallRuleRepository rules,
        IRuleOutcomeRepository outcomes,
        IRetrievalRecordRepository retrievals,
        AgentRecallOptions options)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _outcomes = outcomes ?? throw new ArgumentNullException(nameof(outcomes));
        _retrievals = retrievals ?? throw new ArgumentNullException(nameof(retrievals));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<OutcomeResult> RecordAsync(OutcomeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_options.OutcomeTrackingEnabled)
        {
            return new OutcomeResult { Enabled = false };
        }

        var (ruleIds, retrievalId, error) = await ResolveTargetsAsync(request, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return new OutcomeResult { Enabled = true, Error = error };
        }

        var delta = _options.OutcomeConfidenceDeltas.For(request.Type);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? $"{request.Type} outcome." : request.Reason!.Trim();

        var existing = await _outcomes.ListAsync(cancellationToken).ConfigureAwait(false);
        var adjustments = new List<RuleAdjustment>();
        var skipped = 0;

        foreach (var ruleId in ruleIds)
        {
            var rule = await _rules.GetAsync(ruleId, cancellationToken).ConfigureAwait(false);
            if (rule is null)
            {
                continue;
            }

            // One adjustment per (retrieval, type, rule) unless duplicates are allowed.
            if (!request.AllowDuplicate && IsDuplicate(existing, ruleId, retrievalId, request.Type))
            {
                skipped++;
                continue;
            }

            var previous = rule.Confidence;
            var updated = Math.Round(Math.Clamp(previous + delta, 0.0, 1.0), 2);
            var appliedDelta = Math.Round(updated - previous, 2);

            if (appliedDelta != 0)
            {
                rule.Confidence = updated;
                rule.UpdatedAt = DateTimeOffset.UtcNow;
                await _rules.UpdateAsync(rule, cancellationToken).ConfigureAwait(false);
            }

            await _outcomes.AddAsync(new RuleOutcome
            {
                RuleId = ruleId,
                RetrievalId = retrievalId,
                TaskId = request.TaskId,
                Type = request.Type,
                ConfidenceDelta = appliedDelta,
                Reason = reason,
            }, cancellationToken).ConfigureAwait(false);

            adjustments.Add(new RuleAdjustment
            {
                RuleId = ruleId,
                Type = request.Type,
                PreviousConfidence = previous,
                NewConfidence = updated,
                Delta = appliedDelta,
                Reason = reason,
            });
        }

        return new OutcomeResult { Enabled = true, Adjustments = adjustments, SkippedDuplicates = skipped };
    }

    private async Task<(IReadOnlyList<int> RuleIds, string? RetrievalId, string? Error)> ResolveTargetsAsync(
        OutcomeRequest request,
        CancellationToken cancellationToken)
    {
        // An explicit rule id wins; the retrieval id is still recorded for context.
        if (request.RuleId is { } ruleId)
        {
            return ([ruleId], request.RetrievalId, null);
        }

        if (!string.IsNullOrWhiteSpace(request.RetrievalId))
        {
            var records = await _retrievals.ListAsync(cancellationToken).ConfigureAwait(false);
            var record = records.FirstOrDefault(r =>
                string.Equals(r.RetrievalId, request.RetrievalId, StringComparison.Ordinal));
            if (record is null)
            {
                return ([], request.RetrievalId, $"No retrieval found with id '{request.RetrievalId}'.");
            }

            return (ParseRuleIds(record.RuleIds), request.RetrievalId, null);
        }

        return ([], null, "An outcome needs either --rule-id or --retrieval-id.");
    }

    private static bool IsDuplicate(IReadOnlyList<RuleOutcome> existing, int ruleId, string? retrievalId, OutcomeType type) =>
        existing.Any(o =>
            o.RuleId == ruleId &&
            o.Type == type &&
            string.Equals(o.RetrievalId, retrievalId, StringComparison.Ordinal));

    private static IReadOnlyList<int> ParseRuleIds(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
}
