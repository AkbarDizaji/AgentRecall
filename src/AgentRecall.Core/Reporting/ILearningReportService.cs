namespace AgentRecall.Core.Reporting;

/// <summary>
/// Generates learning reports from local rule and event data only — no LLM calls,
/// no external analytics, no cloud. Every method is deterministic for a given data
/// set and the supplied parameters.
/// </summary>
public interface ILearningReportService
{
    /// <summary>Builds the learning report for the given calendar month.</summary>
    Task<MonthlyLearningReport> GetMonthlyReportAsync(int year, int month, CancellationToken cancellationToken = default);

    /// <summary>Builds cradle-to-grave lifecycle counts across the whole corpus.</summary>
    Task<RuleLifecycleReport> GetLifecycleReportAsync(CancellationToken cancellationToken = default);

    /// <summary>Builds the usage report: retrieval, value, growth, and staleness.</summary>
    Task<LearningUsageReport> GetUsageReportAsync(UsageReportOptions options, CancellationToken cancellationToken = default);

    /// <summary>Distils the project's conventions from the active corpus and retrieval history.</summary>
    Task<ProjectDnaReport> GetDnaReportAsync(int top = 5, CancellationToken cancellationToken = default);
}
