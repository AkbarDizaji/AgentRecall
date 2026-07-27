using AgentRecall.Core.Capture.Judge;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.DocOpportunity;

/// <summary>
/// Maps between the judge's <see cref="DocOpportunityVerdict"/> and the persisted
/// <see cref="DocOpportunityCandidate"/>. <see cref="DocOpportunityVerdict.KeyPoints"/>
/// round-trips as a newline-separated string, so nothing is parsed back out of human prose.
/// </summary>
public static class DocOpportunityMapping
{
    private const char ListSeparator = '\n';

    public static DocOpportunityCandidate ToEntity(DocOpportunityVerdict verdict, string? turnId, string operationHash)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return new DocOpportunityCandidate
        {
            TurnId = turnId ?? string.Empty,
            OperationHash = operationHash,
            DocumentType = verdict.DocumentType,
            Confidence = verdict.Confidence,
            SuggestedTitle = (verdict.SuggestedTitle ?? string.Empty).Trim(),
            Reason = (verdict.Reason ?? string.Empty).Trim(),
            KeyPoints = string.Join(ListSeparator, verdict.KeyPoints),
            Source = "HostSuppliedDocOpportunityJudge",
            Status = DocOpportunityStatus.Open,
        };
    }

    public static IReadOnlyList<string> KeyPoints(DocOpportunityCandidate entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return string.IsNullOrEmpty(entity.KeyPoints)
            ? []
            : entity.KeyPoints.Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
