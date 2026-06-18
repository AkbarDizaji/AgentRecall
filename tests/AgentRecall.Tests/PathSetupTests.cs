using AgentRecall.Cli.Setup;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for PATH self-healing. Only the pure / parameterized pieces are exercised
/// — never <see cref="PathSetup.Ensure"/> — so the suite never touches the real
/// machine's PATH, registry, or shell profiles.
/// </summary>
public class PathSetupTests
{
    private static string NewTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "agentrecall-path-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    [Fact]
    public void PathContains_DetectsPresentAndAbsentEntries()
    {
        var sep = Path.PathSeparator;
        var path = $"/usr/bin{sep}/home/me/.dotnet/tools{sep}/bin";

        Assert.True(PathSetup.PathContains(path, "/home/me/.dotnet/tools"));
        // Trailing-slash differences must not cause a false negative.
        Assert.True(PathSetup.PathContains(path, "/home/me/.dotnet/tools/"));
        Assert.False(PathSetup.PathContains(path, "/home/me/.dotnet"));
        Assert.False(PathSetup.PathContains("", "/home/me/.dotnet/tools"));
    }

    [Fact]
    public void EnsureUnixProfiles_NoProfiles_CreatesProfileWithExportLine()
    {
        var home = NewTempHome();
        try
        {
            var changed = PathSetup.EnsureUnixProfiles(home, "/home/me/.dotnet/tools");

            Assert.Equal([".profile"], changed);

            var content = File.ReadAllText(Path.Combine(home, ".profile"));
            Assert.Contains("export PATH=\"$PATH:/home/me/.dotnet/tools\"", content);
            Assert.Contains("Added by AgentRecall", content);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void EnsureUnixProfiles_ExistingProfile_AppendsAndPreservesContent()
    {
        var home = NewTempHome();
        try
        {
            var bashrc = Path.Combine(home, ".bashrc");
            const string original = "# my bashrc\nalias ll='ls -la'\n";
            File.WriteAllText(bashrc, original);

            var changed = PathSetup.EnsureUnixProfiles(home, "/home/me/.dotnet/tools");

            Assert.Equal([".bashrc"], changed);
            var content = File.ReadAllText(bashrc);
            Assert.StartsWith(original, content); // prior content untouched
            Assert.Contains("/home/me/.dotnet/tools", content);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void EnsureUnixProfiles_IsIdempotent()
    {
        var home = NewTempHome();
        try
        {
            const string dir = "/home/me/.dotnet/tools";
            File.WriteAllText(Path.Combine(home, ".zshrc"), "# zshrc\n");

            var first = PathSetup.EnsureUnixProfiles(home, dir);
            Assert.Equal([".zshrc"], first);

            var second = PathSetup.EnsureUnixProfiles(home, dir);
            Assert.Empty(second); // nothing to add the second time

            var occurrences = File.ReadAllText(Path.Combine(home, ".zshrc")).Split(dir).Length - 1;
            Assert.Equal(1, occurrences);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void ToolsDirectory_IsNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(PathSetup.ToolsDirectory()));
    }
}
