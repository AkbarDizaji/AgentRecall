using System.Diagnostics;
using AgentRecall.Cli.Devcontainer;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Behavioural tests for the self-healing reinstall flow in the generated
/// post-create script. Each test runs the ACTUAL script under bash with fake
/// <c>dotnet</c>, <c>claude</c>, and <c>agentrecall</c> shims on PATH, then asserts
/// what happened: whether a reinstall was attempted, the resulting MCP registration
/// state, warnings, and that startup never blocks. Skipped where bash is unavailable.
/// </summary>
public sealed class DevcontainerSelfHealTests
{
    private static readonly string? Bash =
        new[] { "/bin/bash", "/usr/bin/bash" }.FirstOrDefault(File.Exists);

    private enum DotnetMode { Success, Fail, Crash }

    private sealed record RunResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        string DotnetLog,
        bool Registered,
        string RegistrationTarget,
        bool BinaryInstalled);

    private static async Task<RunResult> RunAsync(bool binaryPreinstalled, DotnetMode dotnet, bool seedStaleRegistration)
    {
        var root = Path.Combine(Path.GetTempPath(), "agentrecall-selfheal", Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var bin = Path.Combine(root, "bin");
        var toolsDir = Path.Combine(home, ".dotnet", "tools");
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(toolsDir);
        Directory.CreateDirectory(dataDir);

        var dotnetLog = Path.Combine(root, "dotnet.log");
        var registry = Path.Combine(root, "registry");        // present == MCP registered
        var agentrecallPath = Path.Combine(toolsDir, "agentrecall");

        const string agentrecallShim =
            "#!/usr/bin/env bash\n" +
            "case \"$1\" in\n" +
            "  --version) echo \"agentrecall 0.4.0\";;\n" +
            "  init) echo \"initialized\";;\n" +
            "  *) ;;\n" +
            "esac\n" +
            "exit 0\n";

        if (binaryPreinstalled)
        {
            WriteExecutable(agentrecallPath, agentrecallShim);
        }

        // Always install a fake `dotnet` shim into bin (first on PATH) so the real
        // dotnet SDK on the host/CI runner is never invoked — the reinstall outcome is
        // controlled entirely by the shim. Success installs the agentrecall shim;
        // Fail/Crash return non-zero (a clean failure vs. a hard crash) without one.
        {
            var installBlock = dotnet switch
            {
                DotnetMode.Success => $"  mkdir -p \"{toolsDir}\"\n  cat > \"{agentrecallPath}\" <<'AR'\n{agentrecallShim}AR\n  chmod +x \"{agentrecallPath}\"\n  exit 0\n",
                DotnetMode.Crash => "  echo \"dotnet: simulated crash\" >&2\n  exit 127\n",
                _ => "  echo \"AgentRecall install failed (simulated)\" >&2\n  exit 1\n",
            };
            WriteExecutable(Path.Combine(bin, "dotnet"),
                "#!/usr/bin/env bash\n" +
                $"echo \"$@\" >> \"{dotnetLog}\"\n" +
                "if [ \"$1\" = \"tool\" ] && [ \"$2\" = \"update\" ]; then\n" +
                installBlock +
                "fi\n" +
                "exit 0\n");
        }

        // Fake claude: `mcp add` records the registered command; `mcp remove` clears it.
        WriteExecutable(Path.Combine(bin, "claude"),
            "#!/usr/bin/env bash\n" +
            $"if [ \"$1\" = \"mcp\" ] && [ \"$2\" = \"add\" ]; then printf '%s' \"$4\" > \"{registry}\"; fi\n" +
            $"if [ \"$1\" = \"mcp\" ] && [ \"$2\" = \"remove\" ]; then rm -f \"{registry}\"; fi\n" +
            "exit 0\n");

        if (seedStaleRegistration)
        {
            await File.WriteAllTextAsync(registry, "stale-registration-from-previous-container");
        }

        var scriptPath = Path.Combine(root, "post-create.sh");
        await File.WriteAllTextAsync(scriptPath, DevcontainerScaffolder.PostCreateScript);

        var psi = new ProcessStartInfo(Bash!, $"\"{scriptPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["HOME"] = home;
        psi.Environment["PATH"] = $"{bin}:/usr/bin:/bin";
        psi.Environment["AGENTRECALL_AgentRecall__DataDirectory"] = dataDir;

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        process.WaitForExit(30_000);

        var result = new RunResult(
            ExitCode: process.ExitCode,
            Stdout: stdout,
            Stderr: stderr,
            DotnetLog: File.Exists(dotnetLog) ? await File.ReadAllTextAsync(dotnetLog) : string.Empty,
            Registered: File.Exists(registry),
            RegistrationTarget: File.Exists(registry) ? await File.ReadAllTextAsync(registry) : string.Empty,
            BinaryInstalled: File.Exists(agentrecallPath));

        try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best effort */ }
        return result;
    }

    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    // 1. Missing binary + reinstall succeeds → reinstall attempted, registration valid, no cleanup.
    [Fact]
    public async Task MissingBinary_ReinstallSucceeds_RegistersAndDoesNotCleanUp()
    {
        if (Bash is null) { return; }

        var r = await RunAsync(binaryPreinstalled: false, DotnetMode.Success, seedStaleRegistration: true);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("attempting reinstall", r.Stderr, StringComparison.Ordinal);
        Assert.Contains("tool update", r.DotnetLog, StringComparison.Ordinal);
        Assert.True(r.BinaryInstalled);
        Assert.True(r.Registered);                                  // still registered (not stripped)
        Assert.EndsWith("agentrecall", r.RegistrationTarget, StringComparison.Ordinal);
        Assert.Contains("MCP server registered", r.Stdout, StringComparison.Ordinal);
    }

    // 2. Missing binary + reinstall fails → reinstall attempted, stale registration removed, warning emitted.
    [Fact]
    public async Task MissingBinary_ReinstallFails_RemovesStaleRegistrationAndWarns()
    {
        if (Bash is null) { return; }

        var r = await RunAsync(binaryPreinstalled: false, DotnetMode.Fail, seedStaleRegistration: true);

        Assert.Equal(0, r.ExitCode);                                // never blocks startup
        Assert.Contains("attempting reinstall", r.Stderr, StringComparison.Ordinal);
        Assert.Contains("tool update", r.DotnetLog, StringComparison.Ordinal);
        Assert.False(r.Registered);                                 // stale registration removed
        Assert.Contains("reinstall failed and the binary is still missing", r.Stderr, StringComparison.Ordinal);
    }

    // 3. Binary exists → no reinstall attempt, no cleanup.
    [Fact]
    public async Task BinaryExists_NoReinstallNoCleanup()
    {
        if (Bash is null) { return; }

        var r = await RunAsync(binaryPreinstalled: true, DotnetMode.Success, seedStaleRegistration: true);

        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain("attempting reinstall", r.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("tool update", r.DotnetLog, StringComparison.Ordinal);  // dotnet never invoked to reinstall
        Assert.True(r.Registered);                                  // registration intact
    }

    // 4. Reinstall command unavailable/crashes → handled, startup continues, stale registration cleaned up.
    [Fact]
    public async Task ReinstallThrows_HandledStartupContinuesAndCleansUp()
    {
        if (Bash is null) { return; }

        var r = await RunAsync(binaryPreinstalled: false, DotnetMode.Crash, seedStaleRegistration: true);

        Assert.Equal(0, r.ExitCode);                                // exception handled, not fatal
        Assert.Contains("attempting reinstall", r.Stderr, StringComparison.Ordinal);
        Assert.False(r.Registered);                                 // stale registration cleaned up
        Assert.Contains("reinstall failed", r.Stderr, StringComparison.Ordinal);
    }

    // 5. Rebuild scenario: binary wiped, stale registration persisted, reinstall succeeds → operational.
    [Fact]
    public async Task RebuildScenario_ReinstallSucceeds_Operational()
    {
        if (Bash is null) { return; }

        var r = await RunAsync(binaryPreinstalled: false, DotnetMode.Success, seedStaleRegistration: true);

        Assert.Equal(0, r.ExitCode);
        Assert.True(r.BinaryInstalled);                             // AgentRecall operational
        Assert.True(r.Registered);                                  // MCP registration valid
        Assert.Contains("AgentRecall ready", r.Stdout, StringComparison.Ordinal);
    }
}
