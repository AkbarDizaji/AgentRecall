using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Evaluation;
using AgentRecall.Core.Search;
using AgentRecall.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentRecall.Cli;

// The `eval` command group: evaluates retrieval quality against a dataset.
public static partial class CommandRouter
{
    private static async Task<int> EvalAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] != "retrieval")
        {
            output.WriteLine("Usage: agentrecall eval retrieval [--dataset <path>]");
            return 1;
        }

        var options = ParseOptions(args);

        EvaluationDataset dataset;
        try
        {
            dataset = options.TryGetValue("dataset", out var path) && !string.IsNullOrWhiteSpace(path)
                ? EvaluationDatasetLoader.LoadFile(path)
                : EvaluationDatasetLoader.LoadDefault();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            output.WriteLine($"Failed to load evaluation dataset: {ex.Message}");
            return 1;
        }

        // Evaluate against an isolated, throwaway store so the user's real DB is
        // never touched or polluted.
        var tempDirectory = Path.Combine(Path.GetTempPath(), "agentrecall-eval", Guid.NewGuid().ToString("N"));
        var evalOptions = new AgentRecallOptions { DataDirectory = tempDirectory, DatabaseFileName = "eval.db" };

        var collection = new ServiceCollection();
        collection.AddSingleton(evalOptions);
        collection.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        collection.AddAgentRecallPersistence();

        await using var provider = collection.BuildServiceProvider();
        try
        {
            RetrievalEvaluationReport report;
            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);
                var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
                var search = scope.ServiceProvider.GetRequiredService<IRecallSearchService>();
                report = await RetrievalEvaluationHarness.RunAsync(dataset, rules, search, cancellationToken).ConfigureAwait(false);
            }

            WriteEvaluationReport(output, report);
            return report.Passed ? 0 : 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of the throwaway store.
            }
        }
    }

    private static void WriteEvaluationReport(TextWriter output, RetrievalEvaluationReport report)
    {
        var m = report.Metrics;
        var b = report.Baseline;

        output.WriteLine($"Retrieval evaluation over {m.ScenarioCount} scenario(s):");
        output.WriteLine($"  Precision@1: {m.PrecisionAt1:0.000}  (baseline {b.PrecisionAt1:0.000})");
        output.WriteLine($"  Precision@3: {m.PrecisionAt3:0.000}  (baseline {b.PrecisionAt3:0.000})");
        output.WriteLine($"  Recall@5:    {m.RecallAt5:0.000}  (baseline {b.RecallAt5:0.000})");

        // Surface scenarios where the expected rule wasn't retrieved in the top 5.
        var misses = report.Scenarios.Where(s => s.RecallAt5 < 1.0).ToList();
        if (misses.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"Misses ({misses.Count}):");
            foreach (var miss in misses)
            {
                var ranked = miss.RankedTopK.Count > 0 ? string.Join(", ", miss.RankedTopK) : "(none)";
                output.WriteLine($"  \"{miss.Query}\" expected [{string.Join(", ", miss.Expected)}] but got [{ranked}]");
            }
        }

        output.WriteLine();
        if (report.Passed)
        {
            output.WriteLine("PASS: retrieval quality meets the baseline.");
        }
        else
        {
            output.WriteLine("FAIL: retrieval quality dropped below the baseline.");
            foreach (var failure in report.Failures)
            {
                output.WriteLine($"  - {failure}");
            }
        }
    }
}
