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
            Assert.Contains("claude mcp add agentrecall agentrecall mcp", script);

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
}
