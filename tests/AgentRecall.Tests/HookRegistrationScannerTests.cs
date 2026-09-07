using AgentRecall.Cli.Devcontainer;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// The scanner answers "is this wired, and wired once?" across the settings files Claude Code
/// merges. Both halves matter: a hook wired in a file nobody inspects reads as missing, and a hook
/// wired in two of them runs twice per turn with no single file showing it.
/// </summary>
public class HookRegistrationScannerTests
{
    private static string NewTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentrecall-scan-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteSettings(string directory, string fileName, string body)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, body);
        return path;
    }

    /// <summary>A settings file registering one command under one hook event.</summary>
    private static string Settings(string hookEvent, string command) =>
        $$"""
        {
          "hooks": {
            "{{hookEvent}}": [
              { "hooks": [ { "type": "command", "command": "{{command}}" } ] }
            ]
          }
        }
        """;

    [Fact]
    public void Scan_SameHookInTwoFiles_IsReportedAsADuplicate()
    {
        var dir = NewTempDirectory();
        try
        {
            var project = WriteSettings(dir, "settings.json", Settings("Stop", "agentrecall finalize-turn --hook"));
            var local = WriteSettings(dir, "settings.local.json", Settings("Stop", "agentrecall finalize-turn --hook"));

            var scan = HookRegistrationScanner.Scan([project, local]);

            var duplicate = Assert.Single(scan.Duplicates);
            Assert.Equal("Stop", duplicate.Key);
            Assert.Equal(2, duplicate.Count());
            Assert.Equal(
                [project, local],
                duplicate.Select(r => r.SettingsPath).ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Different events in different files is the normal shape of a wired project, not a duplicate.
    [Fact]
    public void Scan_DifferentEventsAcrossFiles_IsNotADuplicate()
    {
        var dir = NewTempDirectory();
        try
        {
            var project = WriteSettings(dir, "settings.json", Settings("Stop", "agentrecall finalize-turn --hook"));
            var local = WriteSettings(
                dir, "settings.local.json", Settings("UserPromptSubmit", "agentrecall hook user-prompt-submit"));

            var scan = HookRegistrationScanner.Scan([project, local]);

            Assert.Empty(scan.Duplicates);
            Assert.Equal(2, scan.Registrations.Count);
            Assert.True(scan.Registers(DevcontainerScaffolder.FinalizeTurnMarker));
            Assert.True(scan.Registers(DevcontainerScaffolder.RecallHookMarker));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A PATH prefix is how every scaffolded hook is written, so substring matching is the contract.
    [Fact]
    public void Scan_MatchesCommandsWithAPathPrefix()
    {
        var dir = NewTempDirectory();
        try
        {
            var path = WriteSettings(
                dir,
                "settings.json",
                Settings("Stop", "PATH=$HOME/.dotnet/tools:$PATH agentrecall finalize-turn --hook"));

            var scan = HookRegistrationScanner.Scan([path]);

            Assert.Single(scan.Registrations);
            Assert.True(scan.Registers(DevcontainerScaffolder.FinalizeTurnMarker));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Scan_IgnoresHooksThatAreNotAgentRecalls()
    {
        var dir = NewTempDirectory();
        try
        {
            var path = WriteSettings(dir, "settings.json", Settings("Stop", "bash .ai/hooks/format.sh"));

            var scan = HookRegistrationScanner.Scan([path]);

            Assert.False(scan.Any);
            Assert.Empty(scan.Duplicates);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // An existing file that cannot be parsed is a wiring failure, not an absent option: Claude Code
    // cannot load it either, so its hooks are not running.
    [Fact]
    public void Scan_UnparseableSettingsFile_IsReportedRatherThanIgnored()
    {
        var dir = NewTempDirectory();
        try
        {
            var path = WriteSettings(dir, "settings.json", "{ \"hooks\": ");

            var scan = HookRegistrationScanner.Scan([path]);

            Assert.Equal(path, Assert.Single(scan.UnreadableFiles));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Scan_MissingFiles_AreSimplyAbsent()
    {
        var dir = NewTempDirectory();
        try
        {
            var scan = HookRegistrationScanner.Scan([Path.Combine(dir, "settings.json")]);

            Assert.False(scan.Any);
            Assert.Empty(scan.UnreadableFiles);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The user-level file merges into every project, so it belongs in the scanned set.
    [Fact]
    public void SettingsPathsFor_CoversBothProjectFilesAndTheUserFile()
    {
        var paths = HookRegistrationScanner.SettingsPathsFor("/tmp/project", "/tmp/home/.claude/settings.json");

        Assert.Equal(
            [
                Path.Combine("/tmp/project", ".claude", "settings.json"),
                Path.Combine("/tmp/project", ".claude", "settings.local.json"),
                "/tmp/home/.claude/settings.json",
            ],
            paths.ToArray());
    }

    [Fact]
    public void SettingsPathsFor_WithoutAnOverride_UsesTheUserProfile()
    {
        var paths = HookRegistrationScanner.SettingsPathsFor("/tmp/project");

        Assert.Equal(HookRegistrationScanner.DefaultUserSettingsPath(), paths[^1]);
    }
}
