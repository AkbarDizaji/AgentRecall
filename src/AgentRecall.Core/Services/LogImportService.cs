using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Services;

/// <summary>
/// Default <see cref="ILogImportService"/>. Each detected failure becomes a
/// <see cref="RecallEvent"/>; failures that match an existing rule (by trigger
/// or tag) reinforce that rule, which may auto-promote it.
/// </summary>
public sealed class LogImportService : ILogImportService
{
    private readonly IRecallEventRepository _events;
    private readonly IRecallRuleRepository _rules;
    private readonly IRuleLifecycleService _lifecycle;

    public LogImportService(
        IRecallEventRepository events,
        IRecallRuleRepository rules,
        IRuleLifecycleService lifecycle)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public async Task<ImportResult> ImportAsync(LogKind kind, string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A log file path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Log file not found: {filePath}", filePath);
        }

        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken).ConfigureAwait(false);
        var failures = FailureLogParser.Parse(kind, lines);

        // Snapshot of live rules used only for matching (trigger/tags don't change).
        // Superseded and archived rules are never reinforced.
        var rules = (await _rules.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(r => r.Status is not (RuleStatus.Superseded or RuleStatus.Archived))
            .ToList();
        var promotedBefore = rules.Where(r => r.Status == RuleStatus.Promoted).Select(r => r.Id).ToHashSet();

        var reinforced = new HashSet<int>();
        var newlyPromoted = new HashSet<int>();
        var eventsCreated = 0;

        foreach (var failure in failures)
        {
            var matches = rules.Where(r => Matches(r, failure)).ToList();

            await _events.AddAsync(new RecallEvent
            {
                Type = RecallEventType.MistakeObserved,
                RuleId = matches.Count > 0 ? matches[0].Id : null,
                Trigger = $"{kind} failure",
                Details = failure,
            }, cancellationToken).ConfigureAwait(false);
            eventsCreated++;

            foreach (var match in matches)
            {
                var updated = await _lifecycle
                    .ReinforceAsync(match.Id, RuleLifecycleService.ReinforcementDelta, cancellationToken)
                    .ConfigureAwait(false);

                reinforced.Add(match.Id);
                if (updated.Status == RuleStatus.Promoted && !promotedBefore.Contains(match.Id))
                {
                    newlyPromoted.Add(match.Id);
                }
            }
        }

        return new ImportResult(kind, failures.Count, eventsCreated, reinforced.Count, newlyPromoted.Count);
    }

    /// <summary>A rule matches a failure when its trigger or a tag appears in the line.</summary>
    private static bool Matches(RecallRule rule, string failure)
    {
        var message = failure.ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(rule.Trigger) &&
            message.Contains(rule.Trigger.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var tag in rule.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (message.Contains(tag.ToLowerInvariant(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
