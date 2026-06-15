namespace AgentRecall.Core.Evaluation;

/// <summary>A rule in an evaluation corpus, referenced by a stable <see cref="Key"/>.</summary>
public sealed record EvaluationRule
{
    public required string Key { get; init; }
    public string Trigger { get; init; } = string.Empty;
    public required string Rule { get; init; }
    public string DoNot { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;

    /// <summary>"Active" or "Promoted".</summary>
    public string Status { get; init; } = "Active";
}

/// <summary>A retrieval scenario: a query and the rule key(s) expected to match.</summary>
public sealed record EvaluationScenario
{
    public required string Query { get; init; }
    public required IReadOnlyList<string> Expected { get; init; }
    public string? ScopeLevel { get; init; }
    public string? ScopeValue { get; init; }
}

/// <summary>The minimum acceptable retrieval metrics — the CI gate.</summary>
public sealed record EvaluationBaseline
{
    public double PrecisionAt1 { get; init; }
    public double PrecisionAt3 { get; init; }
    public double RecallAt5 { get; init; }
}

/// <summary>A corpus of rules plus the scenarios and baseline to evaluate against.</summary>
public sealed record EvaluationDataset
{
    public required IReadOnlyList<EvaluationRule> Rules { get; init; }
    public required IReadOnlyList<EvaluationScenario> Scenarios { get; init; }
    public EvaluationBaseline Baseline { get; init; } = new();
}

/// <summary>Per-scenario retrieval outcome.</summary>
public sealed record ScenarioResult
{
    public required string Query { get; init; }
    public required IReadOnlyList<string> Expected { get; init; }
    public required IReadOnlyList<string> RankedTopK { get; init; }
    public required double PrecisionAt1 { get; init; }
    public required double PrecisionAt3 { get; init; }
    public required double RecallAt5 { get; init; }
}

/// <summary>Aggregate (macro-averaged) retrieval metrics across all scenarios.</summary>
public sealed record RetrievalMetrics
{
    public required double PrecisionAt1 { get; init; }
    public required double PrecisionAt3 { get; init; }
    public required double RecallAt5 { get; init; }
    public required int ScenarioCount { get; init; }
}

/// <summary>The full evaluation report, including whether the baseline held.</summary>
public sealed record RetrievalEvaluationReport
{
    public required RetrievalMetrics Metrics { get; init; }
    public required EvaluationBaseline Baseline { get; init; }
    public required bool Passed { get; init; }
    public required IReadOnlyList<ScenarioResult> Scenarios { get; init; }

    /// <summary>Human-readable descriptions of any metric that fell below baseline.</summary>
    public required IReadOnlyList<string> Failures { get; init; }
}
