using System.Text.Json.Nodes;

namespace AgentRecall.Cli.Devcontainer;

/// <summary>
/// One hook AgentRecall registers with Claude Code: the event it binds to, the command that runs,
/// the invariant tail that identifies it in a settings file, and the tails of any earlier command
/// it supersedes.
/// </summary>
/// <param name="Event">The Claude Code event, e.g. <c>Stop</c>.</param>
/// <param name="Command">The command as registered, PATH prefix included.</param>
/// <param name="Marker">The tail that identifies this hook regardless of prefix.</param>
/// <param name="Matcher">The tool matcher, for events that scope to specific tools.</param>
/// <param name="LegacyMarkers">
/// Tails of prior registrations of the same hook. They identify a registration to upgrade in
/// place, which is what keeps re-running init from appending a second copy.
/// </param>
public sealed record AgentRecallHook(
    string Event,
    string Command,
    string Marker,
    string? Matcher = null,
    IReadOnlyList<string>? LegacyMarkers = null)
{
    /// <summary>Every tail that identifies this hook, current first.</summary>
    public IReadOnlyList<string> Markers => [Marker, .. LegacyMarkers ?? []];

    /// <summary>Whether a registered command is this hook, at any version and with any prefix.</summary>
    public bool Matches(string command) =>
        Markers.Any(marker => command.Contains(marker, StringComparison.Ordinal));
}

/// <summary>
/// The hooks AgentRecall owns, and the one way to find them inside Claude Code's settings JSON.
///
/// Both halves are here for the same reason: they were previously duplicated, and the copies could
/// drift. The scaffolder knew which hooks to write while a separate scanner kept its own list of
/// which commands to count, so adding a hook to one list left the other silently blind — and the
/// scanner exists precisely to notice miswiring. The settings traversal was written twice as well,
/// once to find a registration to upgrade and once to count registrations. Adding a hook now means
/// adding one entry to <see cref="All"/>, and everything that writes, counts, or audits hooks
/// follows.
/// </summary>
public static class AgentRecallHooks
{
    /// <summary>Injects the relevant rules on each prompt.</summary>
    public static AgentRecallHook Recall { get; } = new(
        DevcontainerScaffolder.RecallHookEvent,
        DevcontainerScaffolder.HookCommand,
        DevcontainerScaffolder.RecallHookMarker);

    /// <summary>Finalizes the turn once the assistant finishes it.</summary>
    public static AgentRecallHook FinalizeTurn { get; } = new(
        DevcontainerScaffolder.CaptureHookEvent,
        DevcontainerScaffolder.FinalizeTurnHookCommand,
        DevcontainerScaffolder.FinalizeTurnMarker,
        LegacyMarkers: [DevcontainerScaffolder.CaptureHookMarker]);

    /// <summary>Injects the rules relevant to the file about to be written.</summary>
    public static AgentRecallHook PreToolUse { get; } = new(
        DevcontainerScaffolder.PreToolUseHookEvent,
        DevcontainerScaffolder.PreToolUseHookCommand,
        DevcontainerScaffolder.PreToolUseHookMarker,
        Matcher: DevcontainerScaffolder.PreToolUseHookMatcher);

    /// <summary>Every hook AgentRecall wires up, and the list a fourth one gets added to.</summary>
    public static IReadOnlyList<AgentRecallHook> All { get; } = [Recall, FinalizeTurn, PreToolUse];

    /// <summary>Whether a registered command belongs to AgentRecall, at any version.</summary>
    public static bool IsAgentRecall(string command) => All.Any(hook => hook.Matches(command));

    /// <summary>
    /// Yields each <c>{ "type": "command", "command": … }</c> object under one event's matcher
    /// groups, paired with its command, whether or not it is AgentRecall's. Callers filter; the
    /// walk itself is the part worth having in one place.
    /// </summary>
    public static IEnumerable<(JsonObject Hook, string Command)> HookCommandsIn(JsonArray matchers)
    {
        ArgumentNullException.ThrowIfNull(matchers);

        foreach (var matcher in matchers)
        {
            if (matcher?["hooks"] is not JsonArray inner)
            {
                continue;
            }

            foreach (var entry in inner)
            {
                if (entry is JsonObject hook && hook["command"]?.GetValue<string>() is { } command)
                {
                    yield return (hook, command);
                }
            }
        }
    }

    /// <summary>
    /// The hook object registering <paramref name="hook"/> under these matcher groups — current
    /// command or superseded one — or null when it is not registered. Returned as the live
    /// <see cref="JsonObject"/> so a caller can upgrade the command in place.
    /// </summary>
    public static JsonObject? FindRegistration(JsonArray matchers, AgentRecallHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);

        return HookCommandsIn(matchers)
            .Where(found => hook.Matches(found.Command))
            .Select(found => found.Hook)
            .FirstOrDefault();
    }
}
