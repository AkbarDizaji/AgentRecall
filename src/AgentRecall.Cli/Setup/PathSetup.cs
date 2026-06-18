using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AgentRecall.Cli.Setup;

/// <summary>What happened when ensuring the tools directory is on PATH.</summary>
public enum PathSetupOutcome
{
    /// <summary>The tools directory was already on the persisted PATH.</summary>
    AlreadyConfigured,

    /// <summary>The tools directory was added to the persisted PATH.</summary>
    Added,

    /// <summary>PATH could not be updated automatically (reported in Detail).</summary>
    Failed,
}

/// <summary>Result of <see cref="PathSetup.Ensure"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ToolsDirectory">The .NET global tools directory in question.</param>
/// <param name="Detail">Where it was written, or why it failed; null when already configured.</param>
public sealed record PathSetupResult(PathSetupOutcome Outcome, string ToolsDirectory, string? Detail);

/// <summary>
/// Keeps the globally-installed <c>agentrecall</c> discoverable by ensuring its
/// .NET tools directory is on the user's persisted PATH. A .NET global tool has no
/// post-install hook, so this self-healing runs the first time the tool is invoked
/// (and via the explicit <c>setup</c> command). Idempotent and best-effort: it
/// never throws into the caller.
/// </summary>
public static class PathSetup
{
    /// <summary>
    /// The directory the running tool lives in (its apphost path) — the directory
    /// that must be on PATH. Falls back to the conventional <c>~/.dotnet/tools</c>.
    /// </summary>
    public static string ToolsDirectory()
    {
        var processPath = Environment.ProcessPath;
        var dir = string.IsNullOrEmpty(processPath) ? null : Path.GetDirectoryName(processPath);
        if (!string.IsNullOrEmpty(dir))
        {
            return dir;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".dotnet", "tools");
    }

    /// <summary>Ensures the tools directory is on the persisted user PATH.</summary>
    public static PathSetupResult Ensure()
    {
        var dir = ToolsDirectory();
        try
        {
            return OperatingSystem.IsWindows() ? EnsureWindows(dir) : EnsureUnix(dir);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new PathSetupResult(PathSetupOutcome.Failed, dir, ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static PathSetupResult EnsureWindows(string dir)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true)
                        ?? Registry.CurrentUser.CreateSubKey("Environment");

        // Read without expanding %VARS% so we round-trip the literal value.
        var existing = key.GetValue("PATH", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var current = existing ?? string.Empty;

        if (PathContains(current, dir))
        {
            EnsureProcessPath(dir);
            return new PathSetupResult(PathSetupOutcome.AlreadyConfigured, dir, null);
        }

        // Preserve REG_EXPAND_SZ when the existing PATH uses it, so entries like
        // %JAVA_HOME% keep expanding; default new values to expandable too.
        var kind = existing is null ? RegistryValueKind.ExpandString : key.GetValueKind("PATH");
        var updated = current.Length == 0 ? dir : current.TrimEnd(';') + ";" + dir;
        key.SetValue("PATH", updated, kind);

        EnsureProcessPath(dir);
        return new PathSetupResult(PathSetupOutcome.Added, dir, "user PATH");
    }

    private static PathSetupResult EnsureUnix(string dir)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return new PathSetupResult(PathSetupOutcome.Failed, dir, "could not resolve the home directory");
        }

        var updated = EnsureUnixProfiles(home, dir);
        EnsureProcessPath(dir);

        return updated.Count == 0
            ? new PathSetupResult(PathSetupOutcome.AlreadyConfigured, dir, null)
            : new PathSetupResult(PathSetupOutcome.Added, dir, "shell profile(s): " + string.Join(", ", updated));
    }

    /// <summary>
    /// Appends a guarded PATH line to the user's shell profiles that don't already
    /// reference <paramref name="dir"/>. Creates <c>~/.profile</c> when none exist.
    /// Returns the file names that were changed. Pure w.r.t. <paramref name="home"/>
    /// so it can be tested against a temp directory.
    /// </summary>
    internal static IReadOnlyList<string> EnsureUnixProfiles(string home, string dir)
    {
        const string marker = "# Added by AgentRecall: ensure .NET global tools are on PATH";
        var line = $"export PATH=\"$PATH:{dir}\"";

        var candidates = new[] { ".zshrc", ".bashrc", ".profile" }
            .Select(name => Path.Combine(home, name))
            .Where(File.Exists)
            .ToList();

        if (candidates.Count == 0)
        {
            candidates.Add(Path.Combine(home, ".profile"));
        }

        var changed = new List<string>();
        foreach (var profile in candidates)
        {
            var content = File.Exists(profile) ? File.ReadAllText(profile) : string.Empty;
            if (content.Contains(dir, StringComparison.Ordinal))
            {
                continue;
            }

            var separator = content.Length == 0 || content.EndsWith('\n') ? string.Empty : "\n";
            File.AppendAllText(profile, $"{separator}\n{marker}\n{line}\n");
            changed.Add(Path.GetFileName(profile));
        }

        return changed;
    }

    /// <summary>Adds the directory to this process's PATH so later in-run calls see it.</summary>
    private static void EnsureProcessPath(string dir)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (PathContains(current, dir))
        {
            return;
        }

        Environment.SetEnvironmentVariable(
            "PATH", current.Length == 0 ? dir : current + Path.PathSeparator + dir);
    }

    /// <summary>True when <paramref name="path"/> already lists <paramref name="dir"/>.</summary>
    internal static bool PathContains(string path, string dir)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var target = Trim(dir);

        foreach (var part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(Trim(part), target, comparison))
            {
                return true;
            }
        }

        return false;

        static string Trim(string value) =>
            value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
