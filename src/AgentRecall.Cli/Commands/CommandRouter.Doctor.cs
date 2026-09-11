using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using AgentRecall.Core;
using AgentRecall.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentRecall.Cli;

// The `doctor` command: a single health-check pass over the pieces AgentRecall depends on
// (database/schema, PATH, Claude Code hook wiring when the project opts in, and the
// installed CLI version). Read-only by default; --fix repairs what's safely repairable and
// self-upgrades via `dotnet tool update` when a newer version is published on NuGet.
public static partial class CommandRouter
{
    private const string NuGetPackageId = "AgentRecall";
    private const string NuGetVersionsUrl = "https://api.nuget.org/v3-flatcontainer/agentrecall/index.json";

    private enum DoctorStatus { Ok, Warn, Fail }

    private sealed record DoctorCheck(string Name, DoctorStatus Status, string Message, string? FixHint = null);

    private static async Task<int> DoctorAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(args);
        var json = options.ContainsKey("json");
        var fix = options.ContainsKey("fix");
        var offline = options.ContainsKey("offline");
        var projectRoot = options.GetValueOrDefault("project") ?? Directory.GetCurrentDirectory();

        // Which user-level settings file merges into this project. Overridable because the merged
        // view is the whole point of the registration check, and it has to be inspectable for a
        // machine other than the one running the command.
        var userSettingsPath = options.GetValueOrDefault("user-settings");

        var checks = new List<DoctorCheck> { await CheckDatabaseAsync(services, cancellationToken).ConfigureAwait(false), CheckPath(fix) };

        var hooksCheck = CheckHooks(projectRoot, fix, userSettingsPath);
        if (hooksCheck is not null)
        {
            checks.Add(hooksCheck);
        }

        var registrationCheck = CheckHookRegistrations(projectRoot, userSettingsPath);
        if (registrationCheck is not null)
        {
            checks.Add(registrationCheck);
        }

        var sourceCheck = CheckInstalledAgainstSource(projectRoot);
        if (sourceCheck is not null)
        {
            checks.Add(sourceCheck);
        }

        var contractCheck = CheckContract(projectRoot, fix);
        if (contractCheck is not null)
        {
            checks.Add(contractCheck);
        }

        if (!offline)
        {
            checks.Add(await CheckVersionAsync(fix, cancellationToken).ConfigureAwait(false));
        }

        var hasFailure = checks.Any(c => c.Status == DoctorStatus.Fail);

        if (json)
        {
            WriteJson(output, new
            {
                ok = !hasFailure,
                fixApplied = fix,
                checks = checks.Select(c => new
                {
                    name = c.Name,
                    status = c.Status.ToString(),
                    message = c.Message,
                    fix = c.FixHint,
                }),
            });
            return hasFailure ? 1 : 0;
        }

        output.WriteLine(fix ? $"{AppInfo.Name} doctor (applying fixes)" : $"{AppInfo.Name} doctor");
        output.WriteLine();
        foreach (var check in checks)
        {
            output.WriteLine($"{Symbol(check.Status)} {check.Name}: {check.Message}");
            if (check.Status != DoctorStatus.Ok && check.FixHint is not null && !fix)
            {
                output.WriteLine($"    fix: {check.FixHint}");
            }
        }

        output.WriteLine();
        if (hasFailure)
        {
            output.WriteLine("One or more checks failed.");
        }
        else
        {
            var warnings = checks.Count(c => c.Status == DoctorStatus.Warn);
            output.WriteLine(warnings == 0 ? "All checks passed." : $"All checks passed ({warnings} warning(s) above).");
        }

        return hasFailure ? 1 : 0;
    }

    private static string Symbol(DoctorStatus status) => status switch
    {
        DoctorStatus.Ok => "✓",
        DoctorStatus.Warn => "⚠",
        _ => "✗",
    };

    private static async Task<DoctorCheck> CheckDatabaseAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        try
        {
            var initializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
            var path = await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            var count = (await rules.ListAsync(cancellationToken).ConfigureAwait(false)).Count;

            return new DoctorCheck("Database", DoctorStatus.Ok, $"ready at {path} ({count} rule(s); schema reconciled)");
        }
        catch (Exception ex)
        {
            return new DoctorCheck("Database", DoctorStatus.Fail, ex.Message,
                "Check disk space/permissions for the data directory, then re-run.");
        }
    }

    private static DoctorCheck CheckPath(bool fix)
    {
        var dir = Setup.PathSetup.ToolsDirectory();
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        if (Setup.PathSetup.PathContains(currentPath, dir))
        {
            return new DoctorCheck("PATH", DoctorStatus.Ok, $"{dir} is on PATH");
        }

        if (fix)
        {
            var result = Setup.PathSetup.Ensure();
            return result.Outcome switch
            {
                Setup.PathSetupOutcome.Added => new DoctorCheck("PATH", DoctorStatus.Ok, $"added {dir} to {result.Detail}"),
                Setup.PathSetupOutcome.AlreadyConfigured => new DoctorCheck("PATH", DoctorStatus.Ok, $"{dir} already configured"),
                _ => new DoctorCheck("PATH", DoctorStatus.Fail, result.Detail ?? "could not update PATH"),
            };
        }

        return new DoctorCheck("PATH", DoctorStatus.Warn, $"{dir} is not on PATH", "agentrecall setup");
    }

    /// <summary>Whether this directory has already opted in to AgentRecall's Claude Code wiring.</summary>
    private static bool OptedIn(string projectRoot) =>
        Directory.Exists(Path.Combine(projectRoot, ".claude"))
        || File.Exists(Path.Combine(projectRoot, ClaudeCode.ClaudeMdGuidance.RelativePath));

    /// <summary>
    /// Whether the directory is somewhere hook wiring is worth reporting on: already opted in, or a
    /// repository where a missing wire-up would otherwise go unnoticed. A scratch directory gets no
    /// hook checks at all, so `doctor` there never reports a problem it does not have.
    /// </summary>
    private static bool LooksLikeAProject(string projectRoot) =>
        OptedIn(projectRoot) || Directory.Exists(Path.Combine(projectRoot, ".git"));

    /// <summary>
    /// Checks Claude Code hook wiring. Reported for any project that has either already
    /// opted in (a <c>.claude</c> directory or <c>CLAUDE.md</c> present) or looks like one
    /// that could (a <c>.git</c> directory present) — a repo is the common case where a
    /// missing wire-up would otherwise go unnoticed. Returns null only outside any
    /// recognizable project (e.g. a scratch directory), so `doctor` there doesn't report a
    /// false problem.
    /// </summary>
    private static DoctorCheck? CheckHooks(string projectRoot, bool fix, string? userSettingsPath)
    {
        var everOptedIn = OptedIn(projectRoot);
        if (!LooksLikeAProject(projectRoot))
        {
            return null;
        }

        var settingsPath = Path.Combine(projectRoot,
            Devcontainer.DevcontainerScaffolder.ClaudeSettingsRelativePath.Replace('/', Path.DirectorySeparatorChar));

        bool HasAllMarkers()
        {
            if (!File.Exists(settingsPath))
            {
                return false;
            }

            var text = File.ReadAllText(settingsPath);
            return Devcontainer.AgentRecallHooks.All.All(
                hook => text.Contains(hook.Marker, StringComparison.Ordinal));
        }

        if (HasAllMarkers())
        {
            return new DoctorCheck("Claude Code hooks", DoctorStatus.Ok, $"wired in {settingsPath}");
        }

        // Wired, but somewhere other than the file this command writes — a local settings file, or
        // the user-level one. Reporting "not wired" there would be wrong, and letting --fix act on
        // it would add a second registration of a hook that already runs.
        var scan = Devcontainer.HookRegistrationScanner.Scan(
            Devcontainer.HookRegistrationScanner.SettingsPathsFor(projectRoot, userSettingsPath));
        if (Devcontainer.AgentRecallHooks.All.All(scan.Registers))
        {
            var files = scan.Registrations
                .Select(r => r.SettingsPath)
                .Distinct(StringComparer.Ordinal)
                .Select(Path.GetFileName);

            return new DoctorCheck(
                "Claude Code hooks",
                DoctorStatus.Ok,
                $"wired across {string.Join(", ", files)}");
        }

        if (fix)
        {
            Devcontainer.DevcontainerScaffolder.Init(projectRoot, createDevcontainer: false);
            return HasAllMarkers()
                ? new DoctorCheck("Claude Code hooks", DoctorStatus.Ok, $"wired in {settingsPath}")
                : new DoctorCheck("Claude Code hooks", DoctorStatus.Fail, $"could not wire hooks in {settingsPath}");
        }

        var message = everOptedIn
            ? $"not fully wired in {settingsPath}"
            : "not wired for this project — automatic recall/capture won't run";
        return new DoctorCheck("Claude Code hooks", DoctorStatus.Warn, message, "agentrecall claude-code init");
    }

    /// <summary>
    /// Reports how many times AgentRecall's hooks are registered across the settings files Claude
    /// Code merges. A hook wired in both <c>settings.json</c> and <c>settings.local.json</c> is not
    /// a conflict the host resolves — it runs twice on every turn, and no single file shows that.
    /// Deliberately not repaired by --fix: the duplicate could be the copy the user wants kept, and
    /// settings.local.json is usually untracked, so guessing wrong is unrecoverable.
    /// </summary>
    private static DoctorCheck? CheckHookRegistrations(string projectRoot, string? userSettingsPath)
    {
        const string name = "Hook registrations";

        if (!LooksLikeAProject(projectRoot))
        {
            return null;
        }

        var scan = Devcontainer.HookRegistrationScanner.Scan(
            Devcontainer.HookRegistrationScanner.SettingsPathsFor(projectRoot, userSettingsPath));

        // Claude Code cannot read them either, so hooks the user believes are wired are not running.
        if (scan.UnreadableFiles.Count > 0)
        {
            return new DoctorCheck(
                name,
                DoctorStatus.Fail,
                $"could not parse {string.Join(", ", scan.UnreadableFiles)} — Claude Code cannot load it either, "
                    + "so its hooks are not running",
                "fix the JSON in that file");
        }

        // Nothing registered anywhere: CheckHooks already says so, and repeating it is noise.
        if (!scan.Any)
        {
            return null;
        }

        if (scan.Duplicates.Count > 0)
        {
            var detail = scan.Duplicates.Select(duplicate =>
                $"{duplicate.Key} registered {duplicate.Count()} times "
                    + $"({string.Join(", ", duplicate.Select(r => Path.GetFileName(r.SettingsPath)))})");

            return new DoctorCheck(
                name,
                DoctorStatus.Warn,
                $"{string.Join("; ", detail)} — Claude Code merges these files, so each runs every turn",
                "remove the duplicate registration from one of those files");
        }

        return new DoctorCheck(
            name,
            DoctorStatus.Ok,
            $"{scan.Registrations.Count} hook(s) registered once each");
    }

    /// <summary>
    /// On an AgentRecall checkout, compares the running build against the version in the working
    /// tree. This is the drift that cost the most to find: the hooks run the installed tool, so
    /// someone developing a new version can spend a long time watching the old one behave exactly
    /// as it always did. The published-version check cannot see it — unreleased source is newer
    /// than anything on NuGet — and it stays silent outside AgentRecall's own repository, where the
    /// comparison would be meaningless.
    /// </summary>
    private static DoctorCheck? CheckInstalledAgainstSource(string projectRoot)
    {
        const string name = "Installed vs. source";

        var propsPath = Path.Combine(projectRoot, "Directory.Build.props");
        if (!File.Exists(propsPath) || !File.Exists(Path.Combine(projectRoot, "AgentRecall.slnx")))
        {
            return null;
        }

        string propsText;
        try
        {
            propsText = File.ReadAllText(propsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            propsText,
            @"<VersionPrefix>\s*([^<\s]+)\s*</VersionPrefix>",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1));

        if (!match.Success
            || !Version.TryParse(match.Groups[1].Value, out var source)
            || !Version.TryParse(AppInfo.Version, out var running))
        {
            return null;
        }

        if (running < source)
        {
            return new DoctorCheck(
                name,
                DoctorStatus.Warn,
                $"running {AppInfo.Version}, but this checkout is {source} — hooks run the installed tool, "
                    + "not this source",
                "dotnet tool update -g agentrecall --source https://api.nuget.org/v3/index.json");
        }

        return new DoctorCheck(name, DoctorStatus.Ok, $"running {AppInfo.Version} against a {source} checkout");
    }

    /// <summary>
    /// Compares the contract the project's instructions declare against the one this build
    /// implements. This is the check that catches a stale install: the version check only knows
    /// what the newest published release is, while the instructions know what the agent is being
    /// told to do. Offline by design — a machine with no network still needs the answer.
    /// </summary>
    private static DoctorCheck? CheckContract(string projectRoot, bool fix)
    {
        const string name = "Instruction contract";

        var path = Path.Combine(projectRoot, ClaudeCode.ClaudeMdGuidance.RelativePath);
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path);

        // A project that never opted in has nothing to compare against, and saying so would be
        // noise; CheckHooks already reports when the wiring is missing.
        if (!text.Contains(ClaudeCode.ClaudeMdGuidance.Heading, StringComparison.Ordinal))
        {
            return null;
        }

        var declared = Core.AgentContract.ReadDeclaredVersion(text);

        // Instructions this build can refresh: rewrite the block, then report what it now says.
        if (fix && (declared is null || declared < Core.AgentContract.Version))
        {
            ClaudeCode.ClaudeMdGuidance.Ensure(projectRoot);
            declared = Core.AgentContract.ReadDeclaredVersion(File.ReadAllText(path));
        }

        if (declared is null)
        {
            return new DoctorCheck(
                name,
                DoctorStatus.Warn,
                $"{path} declares no contract; this build implements {Core.AgentContract.Version}",
                "agentrecall claude-code init");
        }

        // The install is behind what the agent is being asked to do — the silent-capture failure.
        if (declared > Core.AgentContract.Version)
        {
            return new DoctorCheck(
                name,
                DoctorStatus.Fail,
                $"{path} expects contract {declared}; this build implements {Core.AgentContract.Version} — "
                    + "the agent is being asked for capabilities this CLI does not have",
                "dotnet tool update -g agentrecall");
        }

        if (declared < Core.AgentContract.Version)
        {
            return new DoctorCheck(
                name,
                DoctorStatus.Warn,
                $"{path} declares contract {declared}; this build implements {Core.AgentContract.Version}",
                "agentrecall claude-code init");
        }

        return new DoctorCheck(name, DoctorStatus.Ok, $"contract {declared} matches this build");
    }

    private static async Task<DoctorCheck> CheckVersionAsync(bool fix, CancellationToken cancellationToken)
    {
        var current = AppInfo.Version;

        string latest;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync(NuGetVersionsUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var versions = doc.RootElement.GetProperty("versions").EnumerateArray()
                .Select(v => v.GetString()!)
                .Where(v => !v.Contains('-', StringComparison.Ordinal)) // skip prereleases
                .ToList();

            if (versions.Count == 0)
            {
                return new DoctorCheck("Version", DoctorStatus.Warn, $"running {current}; could not determine the latest published version");
            }

            latest = versions[^1];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new DoctorCheck("Version", DoctorStatus.Warn, $"running {current}; could not check for updates ({ex.Message})");
        }

        if (!Version.TryParse(current, out var currentVersion) || !Version.TryParse(latest, out var latestVersion))
        {
            return new DoctorCheck("Version", DoctorStatus.Warn, $"running {current}; latest published is {latest} (could not compare versions)");
        }

        if (latestVersion <= currentVersion)
        {
            return new DoctorCheck("Version", DoctorStatus.Ok, $"running {current} (latest)");
        }

        if (fix)
        {
            try
            {
                var psi = new ProcessStartInfo("dotnet", $"tool update --global {NuGetPackageId}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };

                using var process = Process.Start(psi);
                if (process is null)
                {
                    return new DoctorCheck("Version", DoctorStatus.Fail, "could not start dotnet to upgrade");
                }

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return process.ExitCode == 0
                    ? new DoctorCheck("Version", DoctorStatus.Ok, $"upgraded {current} -> {latest}")
                    : new DoctorCheck("Version", DoctorStatus.Fail, $"upgrade to {latest} failed (dotnet exited {process.ExitCode})");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                return new DoctorCheck("Version", DoctorStatus.Fail, $"could not run 'dotnet tool update': {ex.Message}");
            }
        }

        return new DoctorCheck("Version", DoctorStatus.Warn, $"running {current}; {latest} is available",
            $"dotnet tool update --global {NuGetPackageId}");
    }
}
