using AgentRecall.Cli;
using AgentRecall.Cli.Devcontainer;
using AgentRecall.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Drives the `doctor` command through the real CLI entry point. Every test passes
/// --offline to avoid a real network call to NuGet, and --project points at a throwaway
/// temp directory so a hook-wiring fix never touches this repo's own .claude directory.
/// </summary>
public class CliDoctorSurfaceTests
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

    private static string NewTempProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentrecall-doctor-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task Doctor_Offline_NoClaudeProject_ChecksDatabaseAndSkipsHooks()
    {
        var root = NewTempProject();
        try
        {
            await using var db = await NewDbAsync();

            var (code, output) = await RunAsync(db, "doctor", "--offline", "--project", root);

            Assert.Equal(0, code);
            Assert.Contains("Database:", output, StringComparison.Ordinal);
            Assert.Contains("ready at", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Claude Code hooks", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Version:", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_Json_IsValidAndShaped()
    {
        var root = NewTempProject();
        try
        {
            await using var db = await NewDbAsync();

            var (code, output) = await RunAsync(db, "doctor", "--offline", "--project", root, "--json");

            Assert.Equal(0, code);
            var node = System.Text.Json.Nodes.JsonNode.Parse(output)!;
            Assert.True(node["ok"]!.GetValue<bool>());
            Assert.False(node["fixApplied"]!.GetValue<bool>());
            var checks = node["checks"]!.AsArray();
            Assert.Contains(checks, c => c!["name"]!.GetValue<string>() == "Database");
            Assert.Contains(checks, c => c!["name"]!.GetValue<string>() == "PATH");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_ProjectWithIncompleteHooks_WarnsButDoesNotFail()
    {
        var root = NewTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".claude"));
            await File.WriteAllTextAsync(Path.Combine(root, ".claude", "settings.json"), "{}");

            await using var db = await NewDbAsync();

            var (code, output) = await RunAsync(db, "doctor", "--offline", "--project", root);

            Assert.Equal(0, code);
            Assert.Contains("Claude Code hooks", output, StringComparison.Ordinal);
            Assert.Contains("not fully wired", output, StringComparison.Ordinal);
            Assert.Contains("agentrecall claude-code init", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_GitRepoNeverOptedIn_WarnsInsteadOfSkipping()
    {
        var root = NewTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));

            await using var db = await NewDbAsync();

            var (code, output) = await RunAsync(db, "doctor", "--offline", "--project", root);

            Assert.Equal(0, code);
            Assert.Contains("Claude Code hooks", output, StringComparison.Ordinal);
            Assert.Contains("not wired for this project", output, StringComparison.Ordinal);
            Assert.Contains("agentrecall claude-code init", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_Fix_WiresMissingHooks()
    {
        var root = NewTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".claude"));
            await File.WriteAllTextAsync(Path.Combine(root, ".claude", "settings.json"), "{}");

            await using var db = await NewDbAsync();

            var (code, output) = await RunAsync(db, "doctor", "--offline", "--project", root, "--fix");

            Assert.Equal(0, code);
            Assert.Contains("Claude Code hooks: wired in", output, StringComparison.Ordinal);

            var settingsText = await File.ReadAllTextAsync(Path.Combine(root, ".claude", "settings.json"));
            Assert.Contains("agentrecall hook user-prompt-submit", settingsText, StringComparison.Ordinal);
            Assert.Contains("agentrecall finalize-turn", settingsText, StringComparison.Ordinal);
            Assert.Contains("agentrecall hook pre-tool-use", settingsText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_WellWiredProject_ReportsHooksOk()
    {
        var root = NewTempProject();
        try
        {
            DevcontainerScaffolder.Init(root, createDevcontainer: false);

            await using var db = await NewDbAsync();

            var (code, output) = await RunAsync(db, "doctor", "--offline", "--project", root);

            Assert.Equal(0, code);
            Assert.Contains("Claude Code hooks: wired in", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
