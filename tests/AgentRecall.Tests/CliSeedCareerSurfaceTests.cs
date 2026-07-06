using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Drives the CLI entry point across the <c>seed</c> and <c>career</c> command groups —
/// the remove/status/show/install rendering branches and the career impact/journal/status
/// paths, in both text and JSON form. These exercise the argument parsing, usage errors,
/// and rendering that the service-level tests don't reach. Everything runs offline against
/// a throwaway SQLite database.
/// </summary>
public class CliSeedCareerSurfaceTests
{
    private const string SeedPack = "tidy-first";

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

    // ---- seed -----------------------------------------------------------------

    [Fact]
    public async Task Seed_NoSubcommand_PrintsUsageAndFails()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "seed");

        Assert.Equal(1, code);
        Assert.Contains("Usage:", output);
        Assert.Contains("seed install", output);
    }

    [Fact]
    public async Task Seed_ShowWithoutPack_PrintsUsageAndFails()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "seed", "show");

        Assert.Equal(1, code);
        Assert.Contains("Usage: agentrecall seed show", output);
    }

    [Fact]
    public async Task Seed_ShowUnknownPack_ReportsAndFails()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "seed", "show", "does-not-exist");

        Assert.Equal(1, code);
        Assert.Contains("Unknown seed pack", output);
    }

    [Fact]
    public async Task Seed_ShowJson_IncludesPackMetadata()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "seed", "show", SeedPack, "--json");

        Assert.Equal(0, code);
        Assert.Contains("\"name\"", output);
        Assert.Contains(SeedPack, output);
        Assert.Contains("\"rules\"", output);
    }

    [Fact]
    public async Task Seed_InstallJson_ReportsAddedRules()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "seed", "install", SeedPack, "--json");

        Assert.Equal(0, code);
        Assert.Contains("\"added\"", output);
        Assert.Contains("\"status\"", output);
    }

    [Fact]
    public async Task Seed_Status_BeforeAndAfterInstall()
    {
        await using var db = await NewDbAsync();

        var (beforeCode, before) = await RunAsync(db, "seed", "status");
        Assert.Equal(0, beforeCode);
        Assert.Contains("Seed pack status:", before);
        Assert.Contains("not installed", before);

        await RunAsync(db, "seed", "install", SeedPack);

        var (afterCode, after) = await RunAsync(db, "seed", "status");
        Assert.Equal(0, afterCode);
        Assert.Contains("Active:", after);
    }

    [Fact]
    public async Task Seed_StatusJson_IsMachineReadable()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "seed", "status", "--json");

        Assert.Equal(0, code);
        Assert.Contains("\"installed\"", output);
        Assert.Contains("\"averageConfidence\"", output);
    }

    [Fact]
    public async Task Seed_RemoveWithoutPack_PrintsUsageAndFails()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "seed", "remove");

        Assert.Equal(1, code);
        Assert.Contains("Usage: agentrecall seed remove", output);
    }

    [Fact]
    public async Task Seed_RemoveUnknownPack_ReportsAndFails()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "seed", "remove", "does-not-exist");

        Assert.Equal(1, code);
        Assert.False(string.IsNullOrWhiteSpace(output));
    }

    [Fact]
    public async Task Seed_InstallThenRemove_ArchivesRules()
    {
        await using var db = await NewDbAsync();
        await RunAsync(db, "seed", "install", SeedPack);

        var (code, output) = await RunAsync(db, "seed", "remove", SeedPack);

        Assert.Equal(0, code);
        Assert.Contains("Removed seed pack", output);
        Assert.Contains("archived", output);
    }

    [Fact]
    public async Task Seed_InstallThenRemoveJson_IsMachineReadable()
    {
        await using var db = await NewDbAsync();
        await RunAsync(db, "seed", "install", SeedPack);

        var (code, output) = await RunAsync(db, "seed", "remove", SeedPack, "--json");

        Assert.Equal(0, code);
        Assert.Contains("\"archived\"", output);
        Assert.Contains("\"changes\"", output);
    }

    // ---- career ---------------------------------------------------------------

    [Fact]
    public async Task Career_NoSubcommand_PrintsUsageAndFails()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "career");

        Assert.Equal(1, code);
        Assert.Contains("Usage:", output);
        Assert.Contains("career impact", output);
    }

    [Fact]
    public async Task Career_Status_RendersWhenPackNotInstalled()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "career", "status");

        Assert.Equal(0, code);
        Assert.Contains("AgentRecall Career Impact", output);
        Assert.Contains("Pack installed:", output);
        Assert.Contains("Last candidate:", output);
    }

    [Fact]
    public async Task Career_StatusJson_IsMachineReadable()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "career", "status", "--json");

        Assert.Equal(0, code);
        Assert.Contains("pack_installed", output);
        Assert.Contains("summary_level", output);
    }

    [Fact]
    public async Task Career_ImpactLast_WithNoCandidate_RendersNoImpactMessage()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "career", "impact", "--last");

        Assert.Equal(0, code);
        Assert.False(string.IsNullOrWhiteSpace(output));
    }

    [Fact]
    public async Task Career_ImpactLastJson_WithNoCandidate_ReportsNotSignificant()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "career", "impact", "--last", "--json");

        Assert.Equal(0, code);
        Assert.Contains("is_significant", output);
    }

    [Fact]
    public async Task Career_JournalLast_WithNoCandidate_RendersNoImpactMessage()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "career", "journal", "--last");

        Assert.Equal(0, code);
        Assert.False(string.IsNullOrWhiteSpace(output));
    }

    [Fact]
    public async Task Career_JournalLastJson_WithNoCandidate_ReportsNotSignificant()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "career", "journal", "--last", "--json");

        Assert.Equal(0, code);
        Assert.Contains("is_significant", output);
    }
}
