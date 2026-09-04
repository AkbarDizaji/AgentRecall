using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentRecall.Core;

/// <summary>
/// The capability contract between a build of AgentRecall and the project instructions that
/// drive it.
///
/// The two can drift apart silently, and that is the failure this type exists to make visible.
/// Hooks run whatever <c>agentrecall</c> is installed on the machine — never the working tree —
/// so a repository's <c>CLAUDE.md</c> can ask the agent for `submit_capture_judgment` and
/// `rule_outcomes` while the installed CLI has neither. Nothing errors: the agent is told to do
/// something the binary cannot accept, capture quietly stops happening, and the only visible
/// symptom is a memory that never grows.
///
/// A version number alone does not answer the question, because what matters is which
/// capabilities exist rather than how the release was numbered. So both sides name a contract:
/// the instructions declare the one they were written for (<see cref="Marker"/>), every injected
/// context block carries the one the running build implements (<see cref="Stamp"/>), and
/// <c>agentrecall doctor</c> compares them.
///
/// Contract history — bump <see cref="Version"/> whenever the agent-facing protocol gains a
/// capability the instructions may come to depend on:
/// <list type="number">
///   <item>Deterministic recall and capture hooks (<c>inject_context</c>, <c>capture_feedback</c>).</item>
///   <item>Semantic capture judge and Stop-hook judgment enforcement (<c>submit_capture_judgment</c>).</item>
///   <item>Reported rule outcomes against a retrieval id (<c>rule_outcomes</c>).</item>
/// </list>
/// </summary>
public static class AgentContract
{
    /// <summary>The contract this build implements. See the type remarks for what each number covers.</summary>
    public const int Version = 3;

    /// <summary>The literal the declaration line starts with, in instructions and in the parser.</summary>
    public const string MarkerPrefix = "AgentRecall contract:";

    /// <summary>The declaration line project instructions carry, naming the contract they were written for.</summary>
    public static string Marker => $"{MarkerPrefix} {Version}";

    /// <summary>
    /// How the running build identifies itself to the agent. It rides on a line the agent already
    /// reads, so a stale install is visible without spending a line of context on every turn.
    /// </summary>
    public static string Stamp => $"agentrecall {AppInfo.Version}, contract {Version}";

    /// <summary>
    /// Reads the contract declared by a block of instruction text, or null when the text declares
    /// none — which is itself meaningful, since instructions written before contract stamping
    /// cannot say what they expect.
    /// </summary>
    public static int? ReadDeclaredVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(
            text,
            $@"{Regex.Escape(MarkerPrefix)}\s*(\d{{1,4}})",
            RegexOptions.None,
            TimeSpan.FromSeconds(1));

        return match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var declared)
            ? declared
            : null;
    }
}
