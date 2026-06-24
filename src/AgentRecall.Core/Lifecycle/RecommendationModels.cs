using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Lifecycle;

/// <summary>Inputs for a recommendation run.</summary>
public sealed record RecommendationQuery
{
    /// <summary>Reference instant for staleness; injected so analysis is deterministic.</summary>
    public required DateTimeOffset AsOf { get; init; }

    /// <summary>Only return recommendations of this type, when set.</summary>
    public RecommendationType? Type { get; init; }

    /// <summary>Restrict analysis to rules at this scope level, when set.</summary>
    public ScopeLevel? ScopeLevel { get; init; }

    /// <summary>Restrict analysis to rules with this scope value, when set.</summary>
    public string? ScopeValue { get; init; }
}
