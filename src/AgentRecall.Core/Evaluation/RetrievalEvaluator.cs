namespace AgentRecall.Core.Evaluation;

/// <summary>
/// Computes retrieval-quality metrics (Precision@1, Precision@3, Recall@5) for a
/// dataset, given a retrieval function that returns ranked rule keys for a query.
/// Pure and deterministic — the retrieval source is injected, so it's easy to test.
/// </summary>
public static class RetrievalEvaluator
{
    private const double Epsilon = 1e-9;

    public static async Task<RetrievalEvaluationReport> EvaluateAsync(
        EvaluationDataset dataset,
        Func<EvaluationScenario, Task<IReadOnlyList<string>>> retrieveAsync)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(retrieveAsync);

        var results = new List<ScenarioResult>();
        foreach (var scenario in dataset.Scenarios)
        {
            var ranked = await retrieveAsync(scenario).ConfigureAwait(false);
            var expected = new HashSet<string>(scenario.Expected, StringComparer.OrdinalIgnoreCase);

            results.Add(new ScenarioResult
            {
                Query = scenario.Query,
                Expected = scenario.Expected,
                RankedTopK = ranked.Take(5).ToList(),
                PrecisionAt1 = PrecisionAt(ranked, expected, 1),
                PrecisionAt3 = PrecisionAt(ranked, expected, 3),
                RecallAt5 = RecallAt(ranked, expected, 5),
            });
        }

        var count = results.Count;
        var metrics = new RetrievalMetrics
        {
            PrecisionAt1 = count == 0 ? 0 : results.Average(r => r.PrecisionAt1),
            PrecisionAt3 = count == 0 ? 0 : results.Average(r => r.PrecisionAt3),
            RecallAt5 = count == 0 ? 0 : results.Average(r => r.RecallAt5),
            ScenarioCount = count,
        };

        var failures = new List<string>();
        Check(failures, "Precision@1", metrics.PrecisionAt1, dataset.Baseline.PrecisionAt1);
        Check(failures, "Precision@3", metrics.PrecisionAt3, dataset.Baseline.PrecisionAt3);
        Check(failures, "Recall@5", metrics.RecallAt5, dataset.Baseline.RecallAt5);

        return new RetrievalEvaluationReport
        {
            Metrics = metrics,
            Baseline = dataset.Baseline,
            Passed = failures.Count == 0,
            Scenarios = results,
            Failures = failures,
        };
    }

    private static void Check(List<string> failures, string name, double actual, double baseline)
    {
        if (actual + Epsilon < baseline)
        {
            failures.Add($"{name} {actual:0.000} fell below baseline {baseline:0.000}.");
        }
    }

    /// <summary>Relevant items in the top <paramref name="k"/>, divided by k.</summary>
    private static double PrecisionAt(IReadOnlyList<string> ranked, HashSet<string> expected, int k)
    {
        var relevant = ranked.Take(k).Count(expected.Contains);
        return relevant / (double)k;
    }

    /// <summary>Relevant items in the top <paramref name="k"/>, divided by total relevant.</summary>
    private static double RecallAt(IReadOnlyList<string> ranked, HashSet<string> expected, int k)
    {
        if (expected.Count == 0)
        {
            return 0;
        }

        var relevant = ranked.Take(k).Count(expected.Contains);
        return relevant / (double)expected.Count;
    }
}
