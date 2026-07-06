using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Drives the <c>eval retrieval</c> command against a crafted dataset so both the
/// pass rendering and the miss/failure rendering are exercised. The command evaluates
/// against its own throwaway store, so these never touch the caller's database.
/// </summary>
public class CliEvalSurfaceTests
{
    private static async Task<TestDatabase> NewDbAsync()
    {
        var db = new TestDatabase();
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        return db;
    }

    private static async Task<(int Code, string Output)> RunAsync(TestDatabase db, params string[] args)
    {
        var writer = new StringWriter();
        var code = await CommandRouter.RunAsync(args, db.Services, writer);
        return (code, writer.ToString());
    }

    [Fact]
    public async Task Eval_WithoutRetrievalSubcommand_PrintsUsage()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "eval");

        Assert.Equal(1, code);
        Assert.Contains("Usage: agentrecall eval retrieval", output);
    }

    [Fact]
    public async Task Eval_MissingDatasetFile_ReportsFailure()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "eval", "retrieval", "--dataset", "/no/such/dataset.json");

        Assert.Equal(1, code);
        Assert.Contains("Failed to load evaluation dataset", output);
    }

    [Fact]
    public async Task Eval_DatasetThatMissesBaseline_ReportsMissesAndFails()
    {
        // The expected key is unrelated to the query, while a different rule matches it,
        // so retrieval misses and the strict baseline is not met.
        var dataset = """
        {
          "rules": [
            { "key": "gardening", "trigger": "gardening", "rule": "Water tomato plants early in the morning." },
            { "key": "database", "trigger": "configuring the database", "rule": "Configure the database connection string in appsettings." }
          ],
          "scenarios": [
            { "query": "configure the database connection string", "expected": ["gardening"] }
          ],
          "baseline": { "precisionAt1": 1.0, "precisionAt3": 1.0, "recallAt5": 1.0 }
        }
        """;

        var dir = Path.Combine(Path.GetTempPath(), "agentrecall-eval-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "dataset.json");
        await File.WriteAllTextAsync(path, dataset);

        try
        {
            await using var db = await NewDbAsync();
            var (code, output) = await RunAsync(db, "eval", "retrieval", "--dataset", path);

            Assert.Equal(1, code);
            Assert.Contains("Retrieval evaluation over 1 scenario", output);
            Assert.Contains("Misses", output);
            Assert.Contains("FAIL", output);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
