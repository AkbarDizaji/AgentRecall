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

    // The drift guard. The scaffolder writes hooks and the scanner counts them, and those used to
    // be two hand-maintained lists: a hook added to one was invisible to the other, which blinded
    // the very check that exists to notice miswiring. Both now read AgentRecallHooks.All, and this
    // asserts the round trip so a fourth hook cannot be half-added.
    [Fact]
    public void EveryHookTheScaffolderWrites_IsFoundByAScan()
    {
        var root = NewTempDirectory();
        try
        {
            DevcontainerScaffolder.EnsureUserPromptSubmitHook(root);
            DevcontainerScaffolder.EnsureCaptureHook(root);
            DevcontainerScaffolder.EnsurePreToolUseHook(root);

            var scan = HookRegistrationScanner.Scan(
                HookRegistrationScanner.SettingsPathsFor(root, Path.Combine(root, "absent.json")));

            Assert.Equal(AgentRecallHooks.All.Count, scan.Registrations.Count);
            Assert.Empty(scan.Duplicates);
            foreach (var hook in AgentRecallHooks.All)
            {
                Assert.True(scan.Registers(hook), $"{hook.Event} hook was written but not found by the scan");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A superseded command still identifies the hook, which is what lets init upgrade it in place
    // rather than appending a second registration beside the broken one.
    [Fact]
    public void LegacyCommands_StillIdentifyTheirHook()
    {
        Assert.True(AgentRecallHooks.FinalizeTurn.Matches(DevcontainerScaffolder.CaptureHookCommand));
        Assert.True(AgentRecallHooks.IsAgentRecall(DevcontainerScaffolder.CaptureHookCommand));
        Assert.Contains(DevcontainerScaffolder.CaptureHookMarker, AgentRecallHooks.FinalizeTurn.Markers);
    }

    [Fact]
    public void IsAgentRecall_RecognisesEveryCurrentCommandAndNothingElse()
    {
        foreach (var hook in AgentRecallHooks.All)
        {
            Assert.True(AgentRecallHooks.IsAgentRecall(hook.Command), hook.Command);
        }

        Assert.False(AgentRecallHooks.IsAgentRecall("bash .ai/hooks/format.sh"));
        Assert.False(AgentRecallHooks.IsAgentRecall("agentrecall rules list"));
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
