using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentRecall.Cli.Devcontainer;

/// <summary>One AgentRecall hook registration, and the settings file it was found in.</summary>
/// <param name="Event">The Claude Code hook event it is bound to (e.g. <c>Stop</c>).</param>
/// <param name="Command">The command line as registered, PATH prefix and all.</param>
/// <param name="SettingsPath">The settings file that registered it.</param>
public sealed record HookRegistration(string Event, string Command, string SettingsPath);

/// <summary>Every AgentRecall hook registration a scan found, plus the files it could not read.</summary>
public sealed record HookRegistrationScan
{
    public IReadOnlyList<HookRegistration> Registrations { get; init; } = [];

    /// <summary>
    /// Settings files that exist but could not be parsed. They matter to the answer: Claude Code
    /// cannot read them either, so hooks a user believes are wired are not running, and a scan
    /// that stayed silent about them would report "wired once" for a file nobody can load.
    /// </summary>
    public IReadOnlyList<string> UnreadableFiles { get; init; } = [];

    /// <summary>Whether anything AgentRecall owns is registered at all.</summary>
    public bool Any => Registrations.Count > 0;

    /// <summary>
    /// Hook events registered more than once. Claude Code merges the settings files rather than
    /// letting the most specific one win, so two registrations of the same command run it twice
    /// per turn — the reason this scan exists.
    /// </summary>
    public IReadOnlyList<IGrouping<string, HookRegistration>> Duplicates =>
        [.. Registrations.GroupBy(r => r.Event).Where(g => g.Count() > 1)];

    /// <summary>True when any registration for <paramref name="marker"/> exists in any file.</summary>
    public bool Registers(string marker) =>
        Registrations.Any(r => r.Command.Contains(marker, StringComparison.Ordinal));
}

/// <summary>
/// Reads AgentRecall's hook registrations out of Claude Code's settings files.
///
/// Checking one file is not enough to answer "is this wired, and wired once?". Claude Code merges
/// <c>settings.json</c>, <c>settings.local.json</c> and the user-level settings, so a hook can be
/// wired in a file nobody thought to look at, or wired twice across two of them — which is not a
/// conflict the host resolves but a hook that genuinely runs twice on every turn. That doubling is
/// invisible from any single file, and the only symptom is a turn quietly doing the same work
/// twice.
/// </summary>
public static class HookRegistrationScanner
{
    /// <summary>
    /// Every command fragment that identifies a hook as AgentRecall's, current or legacy. Matched
    /// as a substring so a PATH prefix or an absolute tool path still counts.
    /// </summary>
    private static readonly string[] Markers =
    [
        DevcontainerScaffolder.RecallHookMarker,
        DevcontainerScaffolder.FinalizeTurnMarker,
        DevcontainerScaffolder.PreToolUseHookMarker,
        DevcontainerScaffolder.CaptureHookMarker,
    ];

    /// <summary>
    /// The settings files Claude Code merges for a project, in the order it reads them. The
    /// user-level file is included because a hook registered there merges into every project.
    /// </summary>
    public static IReadOnlyList<string> SettingsPathsFor(string projectRoot, string? userSettingsPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        var claudeDirectory = Path.Combine(projectRoot, ".claude");
        return
        [
            Path.Combine(claudeDirectory, "settings.json"),
            Path.Combine(claudeDirectory, "settings.local.json"),
            userSettingsPath ?? DefaultUserSettingsPath(),
        ];
    }

    /// <summary>Where Claude Code keeps the user-level settings that merge into every project.</summary>
    public static string DefaultUserSettingsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "settings.json");

    /// <summary>
    /// Scans the given settings files. Missing files are simply absent from the result — only a
    /// file that exists and cannot be parsed is reported, since that is a wiring failure rather
    /// than an unused option.
    /// </summary>
    public static HookRegistrationScan Scan(IEnumerable<string> settingsPaths)
    {
        ArgumentNullException.ThrowIfNull(settingsPaths);

        var registrations = new List<HookRegistration>();
        var unreadable = new List<string>();

        foreach (var path in settingsPaths.Distinct(StringComparer.Ordinal))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                unreadable.Add(path);
                continue;
            }

            if (root is not JsonObject settings || settings["hooks"] is not JsonObject hooks)
            {
                continue;
            }

            foreach (var (eventName, groups) in hooks)
            {
                if (groups is not JsonArray matchers)
                {
                    continue;
                }

                registrations.AddRange(
                    CommandsIn(matchers).Select(command => new HookRegistration(eventName, command, path)));
            }
        }

        return new HookRegistrationScan { Registrations = registrations, UnreadableFiles = unreadable };
    }

    /// <summary>Yields the AgentRecall commands registered under one event's matcher groups.</summary>
    private static IEnumerable<string> CommandsIn(JsonArray matchers)
    {
        foreach (var matcher in matchers)
        {
            if (matcher?["hooks"] is not JsonArray inner)
            {
                continue;
            }

            foreach (var entry in inner)
            {
                if (entry is JsonObject obj
                    && obj["command"]?.GetValue<string>() is { } command
                    && Markers.Any(marker => command.Contains(marker, StringComparison.Ordinal)))
                {
                    yield return command;
                }
            }
        }
    }
}
