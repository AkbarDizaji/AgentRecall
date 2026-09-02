using AgentRecall.Core.Activity;

namespace AgentRecall.Core.Outcomes;

/// <summary>
/// Applies a turn's reported outcomes and records what came of them, in one place both routes
/// share — the end-of-turn payload and the <c>submit_capture_judgment</c> tool. A verdict reaches
/// AgentRecall by either road, and an outcome that only counted on one of them would make the
/// ledger depend on which road the agent happened to take.
/// </summary>
public static class TurnOutcomeReporting
{
    /// <summary>Validates and applies the reports, then records the reported/unreported notices.</summary>
    public static async Task<TurnOutcomeReportResult> ApplyAndRecordAsync(
        ITurnOutcomeReporter reporter,
        IActivityRecorder recorder,
        string? turnId,
        IReadOnlyList<ReportedRuleOutcome> reports,
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentNullException.ThrowIfNull(recorder);

        var report = await reporter.ApplyAsync(turnId, reports, cancellationToken).ConfigureAwait(false);

        foreach (var notice in new[]
                 {
                     ActivityNoticeFactory.ForRuleOutcomesReported(report, source),
                     ActivityNoticeFactory.ForRuleOutcomesUnreported(report, source),
                 })
        {
            if (notice is not null)
            {
                await recorder
                    .RecordAsync(notice with { TurnId = turnId }, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return report;
    }
}
