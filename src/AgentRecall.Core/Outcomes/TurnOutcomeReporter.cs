using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Outcomes;

/// <summary>
/// Default <see cref="ITurnOutcomeReporter"/>.
///
/// Two rules decide what is accepted, and both exist because a self-reported outcome is
/// unverifiable by nature:
///
/// 1. A report only counts for a rule some retrieval actually injected. AgentRecall holds those
///    records, so it can check rather than believe — otherwise an outcome could raise the
///    confidence of a rule that was never in play.
/// 2. Only outcomes an agent genuinely witnesses may be self-reported: whether the user accepted
///    or rejected the work, and whether the rule went unused. A model asserting "the build
///    passed" is not evidence of a build passing; those outcomes belong to whatever actually ran
///    the command, so they are refused here with the reason.
///
/// Everything accepted goes through <see cref="IOutcomeTrackingService"/>, which owns the clamped
/// confidence delta and the duplicate suppression. This class only decides what is admissible.
/// </summary>
public sealed class TurnOutcomeReporter : ITurnOutcomeReporter
{
    /// <summary>Outcomes an agent can observe first-hand, and may therefore report itself.</summary>
    public static readonly IReadOnlySet<OutcomeType> SelfReportable = new HashSet<OutcomeType>
    {
        OutcomeType.UserAccepted,
        OutcomeType.UserRejected,
        OutcomeType.CorrectionRepeated,
        OutcomeType.RuleIgnored,
    };

    private readonly IOutcomeTrackingService _outcomes;
    private readonly IRuleOutcomeRepository _ledger;
    private readonly IRetrievalRecordRepository _retrievals;
    private readonly IAgentRecallActivityRepository _activities;
    private readonly AgentRecallOptions _options;

    public TurnOutcomeReporter(
        IOutcomeTrackingService outcomes,
        IRuleOutcomeRepository ledger,
        IRetrievalRecordRepository retrievals,
        IAgentRecallActivityRepository activities,
        AgentRecallOptions options)
    {
        _outcomes = outcomes ?? throw new ArgumentNullException(nameof(outcomes));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _retrievals = retrievals ?? throw new ArgumentNullException(nameof(retrievals));
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<TurnOutcomeReportResult> ApplyAsync(
        string? turnId,
        IReadOnlyList<ReportedRuleOutcome> reports,
        CancellationToken cancellationToken = default)
    {
        reports ??= [];

        if (!_options.OutcomeTrackingEnabled)
        {
            return new TurnOutcomeReportResult { Disabled = true };
        }

        var injected = await InjectedRuleIdsAsync(turnId, cancellationToken).ConfigureAwait(false);
        var retrievals = await _retrievals.ListAsync(cancellationToken).ConfigureAwait(false);

        var applied = new List<(int, OutcomeType)>();
        var rejected = new List<string>();
        var reportedRules = new HashSet<int>();

        foreach (var report in reports)
        {
            if (!SelfReportable.Contains(report.Outcome))
            {
                rejected.Add(
                    $"{report.Outcome} cannot be self-reported: it has to come from whatever ran the " +
                    $"command. Report {string.Join(", ", SelfReportable)} instead.");
                continue;
            }

            var retrieval = ResolveRetrieval(report, retrievals);
            if (retrieval is null)
            {
                rejected.Add(
                    report.RetrievalId is { Length: > 0 } id
                        ? $"No retrieval {id} is recorded, so there is nothing to attach this outcome to."
                        : $"Rule #{report.RuleId} was not injected by any recorded retrieval, so no " +
                          "outcome can be attributed to it.");
                continue;
            }

            var ruleIds = report.RuleId is { } single ? [single] : ParseRuleIds(retrieval.RuleIds);
            if (report.RuleId is { } target && !ParseRuleIds(retrieval.RuleIds).Contains(target))
            {
                rejected.Add(
                    $"Retrieval {retrieval.RetrievalId} did not inject rule #{target}, so the outcome " +
                    "does not belong to it.");
                continue;
            }

            var result = await _outcomes.RecordAsync(
                new OutcomeRequest
                {
                    RuleId = report.RuleId,
                    RetrievalId = report.RuleId is null ? retrieval.RetrievalId : null,
                    TaskId = turnId,
                    Type = report.Outcome,
                    Reason = string.IsNullOrWhiteSpace(report.Note)
                        ? $"Reported for this turn: {report.Outcome}."
                        : report.Note!.Trim(),
                },
                cancellationToken).ConfigureAwait(false);

            if (result.Error is { Length: > 0 } error)
            {
                rejected.Add(error);
                continue;
            }

            foreach (var adjustment in result.Adjustments)
            {
                applied.Add((adjustment.RuleId, report.Outcome));
                reportedRules.Add(adjustment.RuleId);
            }

            // A duplicate outcome still answers for its rules: it is reported, just not moved again.
            foreach (var ruleId in ruleIds)
            {
                reportedRules.Add(ruleId);
            }
        }

        // A rule answered earlier in the same turn is not outstanding: the ask must not keep
        // naming rules whose outcome is already in the ledger, or a resumed turn is asked twice for
        // the same thing.
        var alreadyInLedger = await AlreadyReportedAsync(turnId, cancellationToken).ConfigureAwait(false);

        return new TurnOutcomeReportResult
        {
            Applied = applied,
            Rejected = rejected,
            Unreported =
            [
                .. injected.Where(id => !reportedRules.Contains(id) && !alreadyInLedger.Contains(id)),
            ],
            TurnUsedRules = injected.Count > 0,
        };
    }

    /// <summary>Rules that already carry an outcome recorded against this turn.</summary>
    private async Task<IReadOnlySet<int>> AlreadyReportedAsync(string? turnId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(turnId))
        {
            return new HashSet<int>();
        }

        var recorded = await _ledger.ListAsync(cancellationToken).ConfigureAwait(false);

        return recorded
            .Where(o => string.Equals(o.TaskId, turnId, StringComparison.Ordinal))
            .Select(o => o.RuleId)
            .ToHashSet();
    }

    /// <summary>
    /// The rules this turn injected, read from the turn's own context-fetched activity. That row is
    /// written by the injection hook, so it is the same list the agent was shown.
    /// </summary>
    private async Task<IReadOnlyList<int>> InjectedRuleIdsAsync(string? turnId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(turnId))
        {
            return [];
        }

        var activities = await _activities.ListByTurnAsync(turnId, cancellationToken).ConfigureAwait(false);

        return
        [
            .. activities
                .Where(a => a.ActivityType == ActivityType.ContextFetched)
                .SelectMany(a => ParseRuleIds(a.RuleIds))
                .Distinct(),
        ];
    }

    /// <summary>
    /// The retrieval a report belongs to: the one it names, else the most recent retrieval that
    /// injected the rule it is about. Newest-first, because a rule injected many times is being
    /// reported on for the turn that just used it.
    /// </summary>
    private static RetrievalRecord? ResolveRetrieval(
        ReportedRuleOutcome report, IReadOnlyList<RetrievalRecord> retrievals)
    {
        if (!string.IsNullOrWhiteSpace(report.RetrievalId))
        {
            return retrievals.FirstOrDefault(r =>
                string.Equals(r.RetrievalId, report.RetrievalId, StringComparison.Ordinal));
        }

        return report.RuleId is { } ruleId
            ? retrievals
                .OrderByDescending(r => r.Id)
                .FirstOrDefault(r => ParseRuleIds(r.RuleIds).Contains(ruleId))
            : null;
    }

    private static IReadOnlyList<int> ParseRuleIds(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => int.TryParse(part, out var id) ? id : 0)
                .Where(id => id > 0)];
}
