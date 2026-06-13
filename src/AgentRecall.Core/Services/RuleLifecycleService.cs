using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Services;

/// <summary>
/// Default <see cref="IRuleLifecycleService"/>. Enforces the status flow
/// Pending → Active → Promoted, tracks versions across supersede, and raises
/// confidence with automatic promotion once a threshold is reached.
/// </summary>
public sealed class RuleLifecycleService : IRuleLifecycleService
{
    /// <summary>Confidence at or above which a rule is promoted automatically.</summary>
    public const double PromoteConfidenceThreshold = 0.8;

    /// <summary>Confidence added each time a rule is reinforced by new evidence.</summary>
    public const double ReinforcementDelta = 0.1;

    private readonly IRecallRuleRepository _rules;
    private readonly IRecallEventRepository _events;

    public RuleLifecycleService(IRecallRuleRepository rules, IRecallEventRepository events)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _events = events ?? throw new ArgumentNullException(nameof(events));
    }

    public async Task<RecallRule> ApproveAsync(int id, CancellationToken cancellationToken = default)
    {
        var rule = await GetOrThrowAsync(id, cancellationToken).ConfigureAwait(false);

        if (rule.Status is not (RuleStatus.Pending or RuleStatus.Draft))
        {
            throw new InvalidOperationException($"Cannot approve a rule with status {rule.Status}.");
        }

        rule.Status = RuleStatus.Active;
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        return await _rules.UpdateAsync(rule, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecallRule> PromoteAsync(int id, CancellationToken cancellationToken = default)
    {
        var rule = await GetOrThrowAsync(id, cancellationToken).ConfigureAwait(false);

        if (rule.Status is not (RuleStatus.Active or RuleStatus.Pending))
        {
            throw new InvalidOperationException($"Cannot promote a rule with status {rule.Status}.");
        }

        rule.Status = RuleStatus.Promoted;
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        return await _rules.UpdateAsync(rule, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SupersedeResult> SupersedeAsync(int oldId, int newId, CancellationToken cancellationToken = default)
    {
        if (oldId == newId)
        {
            throw new InvalidOperationException("A rule cannot supersede itself.");
        }

        var old = await GetOrThrowAsync(oldId, cancellationToken).ConfigureAwait(false);
        var replacement = await GetOrThrowAsync(newId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        old.Status = RuleStatus.Superseded;
        old.SupersededById = newId;
        old.UpdatedAt = now;

        // The replacement is a newer version of the same guidance.
        replacement.Version = Math.Max(replacement.Version, old.Version + 1);
        replacement.UpdatedAt = now;

        await _rules.UpdateAsync(old, cancellationToken).ConfigureAwait(false);
        await _rules.UpdateAsync(replacement, cancellationToken).ConfigureAwait(false);

        await _events.AddAsync(new RecallEvent
        {
            Type = RecallEventType.RuleSuperseded,
            RuleId = oldId,
            Trigger = "supersede",
            Details = $"Rule #{oldId} superseded by rule #{newId}.",
        }, cancellationToken).ConfigureAwait(false);

        return new SupersedeResult(old, replacement);
    }

    public async Task<RecallRule> ArchiveAsync(int id, CancellationToken cancellationToken = default)
    {
        var rule = await GetOrThrowAsync(id, cancellationToken).ConfigureAwait(false);

        rule.Status = RuleStatus.Archived;
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        return await _rules.UpdateAsync(rule, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecallRule> ReinforceAsync(int id, double amount, CancellationToken cancellationToken = default)
    {
        var rule = await GetOrThrowAsync(id, cancellationToken).ConfigureAwait(false);

        // Round to avoid floating-point drift accumulating across reinforcements.
        rule.Confidence = Math.Round(Math.Min(1.0, rule.Confidence + amount), 2);
        rule.UpdatedAt = DateTimeOffset.UtcNow;

        // Automatically promote a sufficiently-confident rule that is still in an
        // earlier state.
        if (rule.Confidence >= PromoteConfidenceThreshold &&
            rule.Status is RuleStatus.Active or RuleStatus.Pending)
        {
            rule.Status = RuleStatus.Promoted;
        }

        return await _rules.UpdateAsync(rule, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RecallRule> GetOrThrowAsync(int id, CancellationToken cancellationToken)
    {
        var rule = await _rules.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return rule ?? throw new KeyNotFoundException($"Rule #{id} not found.");
    }
}
