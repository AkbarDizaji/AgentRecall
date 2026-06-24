using System.Text.Json.Nodes;
using AgentRecall.Cli.Devcontainer;
using Xunit;

namespace AgentRecall.Tests;

public class DevcontainerScaffolderTests
{
    private static string NewTempProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentrecall-devcontainer-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Init_WithNoDevcontainer_CreatesScriptAndManifest()
    {
        var root = NewTempProject();
        try
        {
            var result = DevcontainerScaffolder.Init(root);

            Assert.True(result.CreatedDevcontainerJson);
            Assert.False(result.ScriptOverwritten);
            Assert.Null(result.ManualSteps);

            var scriptPath = Path.Combine(root, DevcontainerScaffolder.PostCreateRelativePath);
            var jsonPath = Path.Combine(root, DevcontainerScaffolder.DevcontainerJsonRelativePath);
            Assert.True(File.Exists(scriptPath));
            Assert.True(File.Exists(jsonPath));

            var script = File.ReadAllText(scriptPath);
            Assert.Contains("dotnet tool update --global AgentRecall", script);
            // The MCP server is registered by ABSOLUTE path so it starts even when
            // Claude Code spawns it with a PATH that lacks ~/.dotnet/tools.
            Assert.Contains("claude mcp add agentrecall \"$AGENTRECALL_BIN\" mcp", script);
            Assert.DoesNotContain("claude mcp add agentrecall agentrecall mcp", script);
            // A missing binary clears any stale registration instead of leaving a
            // server that points at a tool that isn't there.
            Assert.Contains("claude mcp remove agentrecall", script);

            // The ownership fix must not assume sudo exists (minimal images lack it):
            // every sudo use is gated behind a `command -v sudo` check.
            Assert.Contains("command -v sudo", script);
            Assert.DoesNotContain("\n  sudo ", script);
            Assert.DoesNotContain("\nsudo ", script);

            // Makes the tool discoverable in non-login interactive shells, and tells the
            // user the exact remoteEnv snippet to set it permanently.
            Assert.Contains(".bashrc", script);
            Assert.Contains("remoteEnv", script);
            Assert.Contains(".dotnet/tools", script);

            // Step logging + failure trap so a broken rebuild names the failing command.
            Assert.Contains("trap", script);
            Assert.Contains("was NOT installed", script);

            var json = File.ReadAllText(jsonPath);
            Assert.Contains("bash .devcontainer/agentrecall-post-create.sh", json);
            Assert.Contains("source=agentrecall-data", json);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Init_WithExistingManifest_LeavesItUntouchedAndReturnsSteps()
    {
        var root = NewTempProject();
        try
        {
            var jsonPath = Path.Combine(root, DevcontainerScaffolder.DevcontainerJsonRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            const string original = "{ \"name\": \"existing\" }";
            File.WriteAllText(jsonPath, original);

            var result = DevcontainerScaffolder.Init(root);

            Assert.False(result.CreatedDevcontainerJson);
            Assert.NotNull(result.ManualSteps);
            Assert.Contains("postCreateCommand", result.ManualSteps);

            // The existing manifest must be preserved verbatim.
            Assert.Equal(original, File.ReadAllText(jsonPath));

            // The setup script is still written, since it never clobbers post-create.sh.
            Assert.True(File.Exists(Path.Combine(root, DevcontainerScaffolder.PostCreateRelativePath)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Init_RerunOverExistingScript_ReportsOverwrite()
    {
        var root = NewTempProject();
        try
        {
            DevcontainerScaffolder.Init(root);
            var second = DevcontainerScaffolder.Init(root);

            Assert.True(second.ScriptOverwritten);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Init_WithNoClaudeSettings_CreatesHook()
    {
        var root = NewTempProject();
        try
        {
            var result = DevcontainerScaffolder.Init(root);

            Assert.Equal(HookSetupOutcome.Created, result.HookOutcome);

            var settingsPath = Path.Combine(root, DevcontainerScaffolder.ClaudeSettingsRelativePath);
            var node = JsonNode.Parse(File.ReadAllText(settingsPath))!;
            var command = node["hooks"]!["UserPromptSubmit"]![0]!["hooks"]![0]!["command"]!.GetValue<string>();
            Assert.Equal(DevcontainerScaffolder.HookCommand, command);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnsureHook_PreservesExistingSettingsAndIsIdempotent()
    {
        var root = NewTempProject();
        try
        {
            var settingsPath = Path.Combine(root, DevcontainerScaffolder.ClaudeSettingsRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, "{ \"model\": \"opus\", \"hooks\": { \"Stop\": [] } }");

            var first = DevcontainerScaffolder.EnsureUserPromptSubmitHook(root);
            Assert.Equal(HookSetupOutcome.Merged, first);

            var node = JsonNode.Parse(File.ReadAllText(settingsPath))!;
            // Unrelated settings survive the merge.
            Assert.Equal("opus", node["model"]!.GetValue<string>());
            Assert.NotNull(node["hooks"]!["Stop"]);
            Assert.NotNull(node["hooks"]!["UserPromptSubmit"]);

            // A second run is a no-op.
            var second = DevcontainerScaffolder.EnsureUserPromptSubmitHook(root);
            Assert.Equal(HookSetupOutcome.AlreadyPresent, second);

            // Still exactly one matcher — no duplicate appended.
            var matchers = node["hooks"]!["UserPromptSubmit"]!.AsArray();
            Assert.Single(matchers);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HookCommand_PutsToolsDirectoryOnPath()
    {
        // Claude Code runs hooks via a non-login /bin/sh that may not have
        // ~/.dotnet/tools on PATH, so a bare `agentrecall` fails with "command not
        // found". The scaffolded command must prepend the global-tools directory.
        Assert.StartsWith("PATH=$HOME/.dotnet/tools:$PATH ", DevcontainerScaffolder.HookCommand);
        Assert.EndsWith(DevcontainerScaffolder.RecallHookMarker, DevcontainerScaffolder.HookCommand);
        Assert.StartsWith("PATH=$HOME/.dotnet/tools:$PATH ", DevcontainerScaffolder.CaptureHookCommand);
        Assert.EndsWith(DevcontainerScaffolder.CaptureHookMarker, DevcontainerScaffolder.CaptureHookCommand);

        // No double quotes — they would be escaped in settings.json and break the
        // shell-portable, machine-independent form.
        Assert.DoesNotContain('"', DevcontainerScaffolder.HookCommand);
        Assert.DoesNotContain('"', DevcontainerScaffolder.CaptureHookCommand);
    }

    [Fact]
    public void EnsureHook_UpgradesOlderBareCommandInPlace()
    {
        var root = NewTempProject();
        try
        {
            // Simulate a project scaffolded by an older AgentRecall: the bare command
            // that the host shell can't resolve (the "command not found" bug).
            var settingsPath = Path.Combine(root, DevcontainerScaffolder.ClaudeSettingsRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(
                settingsPath,
                "{ \"hooks\": { \"Stop\": [ { \"hooks\": [ { \"type\": \"command\", \"command\": \"agentrecall hook capture\" } ] } ] } }");

            var outcome = DevcontainerScaffolder.EnsureCaptureHook(root);
            Assert.Equal(HookSetupOutcome.Merged, outcome);

            var node = JsonNode.Parse(File.ReadAllText(settingsPath))!;
            var matchers = node["hooks"]!["Stop"]!.AsArray();

            // Upgraded in place — still a single matcher, now the PATH-robust command.
            Assert.Single(matchers);
            var command = matchers[0]!["hooks"]![0]!["command"]!.GetValue<string>();
            Assert.Equal(DevcontainerScaffolder.CaptureHookCommand, command);

            // And it's now idempotent against the upgraded form.
            Assert.Equal(HookSetupOutcome.AlreadyPresent, DevcontainerScaffolder.EnsureCaptureHook(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Init_WithNoClaudeMd_CreatesGuidance()
    {
        var root = NewTempProject();
        try
        {
            var result = DevcontainerScaffolder.Init(root);

            Assert.Equal(GuidanceOutcome.Created, result.GuidanceOutcome);

            var claudeMd = File.ReadAllText(Path.Combine(root, DevcontainerScaffolder.ClaudeMdRelativePath));
            Assert.Contains(DevcontainerScaffolder.ClaudeMdHeading, claudeMd);
            // Encodes the accept-on-action capture policy.
            Assert.Contains("accepted", claudeMd);
            Assert.Contains("import_pr_comments", claudeMd);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnsureGuidance_AppendsToExistingFileAndIsIdempotent()
    {
        var root = NewTempProject();
        try
        {
            var path = Path.Combine(root, DevcontainerScaffolder.ClaudeMdRelativePath);
            const string original = "# My Project\n\nExisting notes.\n";
            File.WriteAllText(path, original);

            var first = DevcontainerScaffolder.EnsureClaudeMdGuidance(root);
            Assert.Equal(GuidanceOutcome.Appended, first);

            var afterFirst = File.ReadAllText(path);
            Assert.StartsWith(original, afterFirst); // prior content preserved verbatim
            Assert.Contains(DevcontainerScaffolder.ClaudeMdHeading, afterFirst);

            var second = DevcontainerScaffolder.EnsureClaudeMdGuidance(root);
            Assert.Equal(GuidanceOutcome.AlreadyPresent, second);

            // Heading appears exactly once — no duplicate block.
            var occurrences = File.ReadAllText(path).Split(DevcontainerScaffolder.ClaudeMdHeading).Length - 1;
            Assert.Equal(1, occurrences);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnsureHook_WithMalformedSettings_LeavesFileUntouched()
    {
        var root = NewTempProject();
        try
        {
            var settingsPath = Path.Combine(root, DevcontainerScaffolder.ClaudeSettingsRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            const string garbage = "{ this is not json";
            File.WriteAllText(settingsPath, garbage);

            var outcome = DevcontainerScaffolder.EnsureUserPromptSubmitHook(root);

            Assert.Equal(HookSetupOutcome.SettingsUnparseable, outcome);
            Assert.Equal(garbage, File.ReadAllText(settingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
