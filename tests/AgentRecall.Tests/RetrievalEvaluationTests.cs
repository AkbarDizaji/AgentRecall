using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class RetrievalEvaluationTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    // ---- Metric math (pure) ---------------------------------------------------

    [Fact]
    public async Task Evaluator_ComputesPrecisionAndRecall()
    {
        var dataset = new EvaluationDataset
        {
            Rules = [],
            Scenarios =
            [
                new EvaluationScenario { Query = "a", Expected = ["x"] }, // hit at rank 1
                new EvaluationScenario { Query = "b", Expected = ["y"] }, // hit at rank 4 (in top5, not top3/top1)
                new EvaluationScenario { Query = "c", Expected = ["z"] }, // miss entirely
            ],
        };

        var rankings = new Dictionary<string, IReadOnlyList<string>>
        {
            ["a"] = ["x", "p", "q"],
            ["b"] = ["p", "q", "r", "y", "s"],
            ["c"] = ["p", "q", "r"],
        };

        var report = await RetrievalEvaluator.EvaluateAsync(dataset, s => Task.FromResult(rankings[s.Query]));

        // P@1: only "a" has the expected at rank 1 → 1/3.
        Assert.Equal(1.0 / 3.0, report.Metrics.PrecisionAt1, 5);
        // P@3: only "a" has it in top3 (1 relevant / 3) → (1/3 + 0 + 0) / 3.
        Assert.Equal((1.0 / 3.0) / 3.0, report.Metrics.PrecisionAt3, 5);
        // R@5: "a" and "b" found (1.0 each), "c" missed → 2/3.
        Assert.Equal(2.0 / 3.0, report.Metrics.RecallAt5, 5);
    }

    [Fact]
    public async Task Evaluator_FlagsBaselineFailure()
    {
        var dataset = new EvaluationDataset
        {
            Rules = [],
            Scenarios = [new EvaluationScenario { Query = "a", Expected = ["x"] }],
            Baseline = new EvaluationBaseline { PrecisionAt1 = 1.0, PrecisionAt3 = 0.0, RecallAt5 = 1.0 },
        };

        var report = await RetrievalEvaluator.EvaluateAsync(
            dataset, _ => Task.FromResult<IReadOnlyList<string>>(["wrong"]));

        Assert.False(report.Passed);
        Assert.Contains(report.Failures, f => f.Contains("Precision@1"));
    }

    // ---- Dataset integrity ----------------------------------------------------

    [Fact]
    public void DefaultDataset_HasAtLeast20Scenarios_WithValidExpectedKeys()
    {
        var dataset = EvaluationDatasetLoader.LoadDefault();

        Assert.True(dataset.Scenarios.Count >= 20, $"Expected >= 20 scenarios, found {dataset.Scenarios.Count}.");

        var keys = dataset.Rules.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var scenario in dataset.Scenarios)
        {
            Assert.NotEmpty(scenario.Expected);
            foreach (var expected in scenario.Expected)
            {
                Assert.Contains(expected, keys);
            }
        }
    }

    // ---- The CI gate: real retrieval must meet the baseline -------------------

    [Fact]
    public async Task DefaultDataset_RetrievalMeetsBaseline()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var dataset = EvaluationDatasetLoader.LoadDefault();

        await using var scope = db.CreateScope();
        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var search = scope.ServiceProvider.GetRequiredService<IRecallSearchService>();

        var report = await RetrievalEvaluationHarness.RunAsync(dataset, rules, search);

        Assert.True(
            report.Passed,
            $"Retrieval below baseline. P@1={report.Metrics.PrecisionAt1:0.000}, " +
            $"P@3={report.Metrics.PrecisionAt3:0.000}, R@5={report.Metrics.RecallAt5:0.000}. " +
            $"Failures: {string.Join("; ", report.Failures)}");
    }

    [Fact]
    public async Task ExampleScenarios_RetrieveExpectedRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var dataset = EvaluationDatasetLoader.LoadDefault();

        await using var scope = db.CreateScope();
        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var search = scope.ServiceProvider.GetRequiredService<IRecallSearchService>();

        var report = await RetrievalEvaluationHarness.RunAsync(dataset, rules, search);

        // The two headline examples must be the top-1 result.
        AssertTopHit(report, "write Moq tests", "moq-matchers");
        AssertTopHit(report, "create API route", "prisma-singleton");
    }

    private static void AssertTopHit(RetrievalEvaluationReport report, string query, string expectedKey)
    {
        var scenario = report.Scenarios.Single(s => s.Query == query);
        Assert.Equal(1.0, scenario.PrecisionAt1);
        Assert.Equal(expectedKey, scenario.RankedTopK[0]);
    }

    // ---- CLI command ----------------------------------------------------------

    [Fact]
    public async Task Cli_EvalRetrieval_PrintsMetricsAndPasses()
    {
        // eval builds its own throwaway store; the passed provider is only used for
        // logging, so any provider works here.
        await using var db = new TestDatabase();
        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["eval", "retrieval"], db.Services, output);

        var text = output.ToString();
        Assert.Equal(0, code);
        Assert.Contains("Precision@1", text);
        Assert.Contains("Recall@5", text);
        Assert.Contains("PASS", text);
    }
}
