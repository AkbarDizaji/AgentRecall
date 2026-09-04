using System.Globalization;
using AgentRecall.Cli;
using AgentRecall.Cli.Devcontainer;
using AgentRecall.Core;
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

    /// <summary>Writes a CLAUDE.md whose AgentRecall block declares the given contract.</summary>
    private static Task WriteInstructionsAsync(string root, string? declaredContract) =>
        File.WriteAllTextAsync(
            Path.Combine(root, DevcontainerScaffolder.ClaudeMdRelativePath),
            $"{DevcontainerScaffolder.ClaudeMdHeading}\n\n"
                + (declaredContract is null ? "" : $"**AgentRecall contract: {declaredContract}**\n\n")
                + "Guidance body.\n");

    // The check that catches a stale install: instructions asking for capabilities the running
    // build does not implement is a hard failure, because the agent will be told to call tools
    // that do not exist and nothing else in the system reports it.
    [Fact]
    public async Task Doctor_InstructionsExpectALaterContract_FailsAndPointsAtTheUpdate()
    {
        var root = NewTempProject();
        try
        {
            await WriteInstructionsAsync(root, (AgentContract.Version + 1).ToString(CultureInfo.InvariantCulture));

            await using var db = await NewDbAsync();

            var (code, output) = await RunAsync(db, "doctor", "--offline", "--project", root);

            Assert.Equal(1, code);
            Assert.Contains("Instruction contract", output, StringComparison.Ordinal);
            Assert.Contains($"expects contract {AgentContract.Version + 1}", output, StringComparison.Ordinal);
            Assert.Contains("dotnet tool update -g agentrecall", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_InstructionsWithoutAContract_WarnsAndFixRefreshesThem()
    {
        var root = NewTempProject();
        try
        {
            await WriteInstructionsAsync(root, declaredContract: null);

            await using var db = await NewDbAsync();

            var (warnCode, warnOutput) = await RunAsync(db, "doctor", "--offline", "--project", root);

            Assert.Equal(0, warnCode);
            Assert.Contains("declares no contract", warnOutput, StringComparison.Ordinal);
            Assert.Contains("agentrecall claude-code init", warnOutput, StringComparison.Ordinal);

            var (fixCode, fixOutput) = await RunAsync(db, "doctor", "--offline", "--project", root, "--fix");

            Assert.Equal(0, fixCode);
            Assert.Contains($"contract {AgentContract.Version} matches this build", fixOutput, StringComparison.Ordinal);

            var refreshed = await File.ReadAllTextAsync(
                Path.Combine(root, DevcontainerScaffolder.ClaudeMdRelativePath));
            Assert.Equal(AgentContract.Version, AgentContract.ReadDeclaredVersion(refreshed));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A project that never opted in has nothing to compare, and a check that fires there would be
    // noise on every unrelated repository.
    [Fact]
    public async Task Doctor_ProjectWithoutAgentRecallInstructions_SkipsTheContractCheck()
    {
        var root = NewTempProject();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, DevcontainerScaffolder.ClaudeMdRelativePath),
                "# Someone else's instructions\n");

            await using var db = await NewDbAsync();

            var (code, output) = await RunAsync(db, "doctor", "--offline", "--project", root);

            Assert.Equal(0, code);
            Assert.DoesNotContain("Instruction contract", output, StringComparison.Ordinal);
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
