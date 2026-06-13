using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Search;

/// <summary>A ranked search hit.</summary>
public sealed record SearchResult
{
    public required RecallRule Rule { get; init; }

    /// <summary>Composite ranking score (relevance + status + confidence).</summary>
    public required double Score { get; init; }

    /// <summary>Textual relevance of the rule to the query, 0.0–1.0.</summary>
    public required double Relevance { get; init; }
}

/// <summary>Options controlling a search.</summary>
public sealed record SearchOptions
{
    /// <summary>Maximum number of results to return.</summary>
    public int Limit { get; init; } = 20;

    /// <summary>Minimum relevance a rule must have to be included.</summary>
    public double MinRelevance { get; init; } = 0.0001;

    /// <summary>When set, restrict results to this scope level.</summary>
    public ScopeLevel? ScopeLevel { get; init; }

    /// <summary>When set, restrict results to this scope value (case-insensitive).</summary>
    public string? ScopeValue { get; init; }
}
