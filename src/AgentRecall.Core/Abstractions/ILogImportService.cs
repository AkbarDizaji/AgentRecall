using AgentRecall.Core.Services;

namespace AgentRecall.Core.Abstractions;

/// <summary>Summary of what a log import produced.</summary>
public sealed record ImportResult(
    LogKind Kind,
    int FailuresFound,
    int EventsCreated,
    int RulesReinforced,
    int RulesPromoted);

/// <summary>
/// Ingests failure logs (build/test/lint): records each failure as a
/// <see cref="Domain.RecallEvent"/> and reinforces any rules it matches.
/// </summary>
public interface ILogImportService
{
    Task<ImportResult> ImportAsync(LogKind kind, string filePath, CancellationToken cancellationToken = default);
}
