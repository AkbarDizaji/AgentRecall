using AgentRecall.Core.Capture.Judge;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.DocOpportunity;

/// <summary>A request to run the document-opportunity judge for one finalized turn.</summary>
public sealed record DocOpportunityTurnRequest
{
    public string? Prompt { get; init; }
    public string? Response { get; init; }
    public string? TurnId { get; init; }
    public string Source { get; init; } = "cli";
    public ScopeLevel ScopeLevel { get; init; }
    public string? ScopeValue { get; init; }

    /// <summary>The verdict the host already produced for this turn, when supplied on the payload.</summary>
    public DocOpportunityVerdict? SuppliedVerdict { get; init; }
}
