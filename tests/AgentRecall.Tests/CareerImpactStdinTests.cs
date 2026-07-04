using System.Text.Json.Nodes;
using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Seeds;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Career-impact integration on the finalize-turn (Stop-hook) path, which reads the payload
/// from <see cref="Console.In"/>. These redirect the process-global stdin, so they live in the
/// serialized ConsoleStdin collection. They assert the detector runs at end-of-turn, never
/// blocks, prints the compact summary only on the human path, and keeps the full summary out
/// of the model-visible hook output.
/// </summary>
[Collection("ConsoleStdin")]
public class CareerImpactStdinTests
{
    private static async Task<TestDatabase> NewDbAsync(Action<Core.Configuration.AgentRecallOptions>? configure = null)
    {
        var db = new TestDatabase(configure);
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        await scope.ServiceProvider.GetRequiredService<ISeedPackService>()
            .InstallAsync(CareerImpactSeedPack.Name, new SeedInstallOptions());
        return db;
    }

    private static async Task<(int Code, string Output)> RunWithStdinAsync(
        TestDatabase db, string stdin, params string[] args)
    {
        var originalIn = Console.In;
        var writer = new StringWriter();
        try
        {
            Console.SetIn(new StringReader(stdin));
            var code = await CommandRouter.RunAsync(args, db.Services, writer);
            return (code, writer.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    private static string Payload(string prompt, string response)
    {
        var cwd = Path.Combine(Path.GetTempPath(), "career-impact-turn");
        return new JsonObject
        {
            ["cwd"] = cwd,
            ["source"] = "stop_hook",
            ["prompt"] = prompt,
            ["assistant_response"] = response,
        }.ToJsonString();
    }

    private const string SignificantPrompt =
        "Plan and execute the database migration to the new platform architecture, optimizing latency";
    private const string SignificantResponse =
        "Completed the migration and reduced latency; rolled it out across teams with new metrics.";

    [Fact] // K (via finalize). A significant turn prints the compact career summary on the human path.
    public async Task Finalize_SignificantTurn_PrintsCompactSummary()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunWithStdinAsync(db, Payload(SignificantPrompt, SignificantResponse), "finalize-turn");

        Assert.Equal(0, code);
        Assert.Contains("AgentRecall Career Impact", output, StringComparison.Ordinal);
        Assert.Contains("career journal --last", output, StringComparison.Ordinal);
        // AA: the full journal is never generated automatically.
        Assert.DoesNotContain("# Career Journal Entry", output, StringComparison.Ordinal);
    }

    [Fact] // J, AJ (via finalize). A trivial turn prints no career summary.
    public async Task Finalize_TrivialTurn_PrintsNoCareerSummary()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunWithStdinAsync(db, Payload("Fix a typo in the README", "Fixed the typo."), "finalize-turn");

        Assert.Equal(0, code);
        Assert.DoesNotContain("AgentRecall Career Impact", output, StringComparison.Ordinal);
    }

    [Fact] // V. Finalize-turn never blocks: it exits 0 even with the detector enabled.
    public async Task Finalize_NeverBlocks()
    {
        await using var db = await NewDbAsync();
        var (code, _) = await RunWithStdinAsync(db, Payload(SignificantPrompt, SignificantResponse), "finalize-turn");
        Assert.Equal(0, code);
    }

    [Fact] // W. The hook (model-visible) path emits only a pointer, never the full career summary.
    public async Task Finalize_Hook_DoesNotInjectFullSummary()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunWithStdinAsync(db, Payload(SignificantPrompt, SignificantResponse), "finalize-turn", "--hook");

        Assert.Equal(0, code);
        // A short pointer is allowed in the Turn Memory Summary...
        Assert.Contains("possible Staff-level impact detected", output, StringComparison.Ordinal);
        // ...but never the full compact/detailed career summary.
        Assert.DoesNotContain("AgentRecall Career Impact:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Why it matters", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Evidence to collect", output, StringComparison.Ordinal);
    }

    [Fact] // W (persistence). The detector persists a candidate that `career impact --last` reads back.
    public async Task Finalize_PersistsCandidate_ReadableByCommand()
    {
        await using var db = await NewDbAsync();
        await RunWithStdinAsync(db, Payload(SignificantPrompt, SignificantResponse), "finalize-turn", "--hook");

        var writer = new StringWriter();
        var code = await CommandRouter.RunAsync(["career", "impact", "--last"], db.Services, writer);
        Assert.Equal(0, code);
        Assert.Contains("AgentRecall Career Impact", writer.ToString(), StringComparison.Ordinal);
    }
}
