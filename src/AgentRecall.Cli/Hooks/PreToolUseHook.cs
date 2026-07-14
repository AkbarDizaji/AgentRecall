using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Hooks;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Hooks;

/// <summary>
/// Runs the PreToolUse hook: when the agent is about to write or edit a file, it
/// recalls the rules relevant to that file (keyed on the file's path and the code
/// being written, not the turn's opening prompt) and returns a compact context block
/// to inject just before the write.
///
/// This closes the gap the UserPromptSubmit hook leaves: a high-level prompt like
/// "implement login feature" carries no signal that a service class is coming, so
/// prompt-time recall can't surface a services convention. Recall keyed on the actual
/// artifact does — at the moment it matters.
///
/// It never throws: on any failure it logs to <paramref name="diagnostics"/> and
/// returns empty, so a write is never blocked.
/// </summary>
public static class PreToolUseHook
{
    // The tools that mutate a file's contents. Read/search/run tools carry no artifact
    // to recall against, so they are ignored.
    private static readonly HashSet<string> FileMutatingTools =
        new(StringComparer.Ordinal) { "Edit", "Write", "MultiEdit" };

    // The code snippet fed into the recall query is bounded: enough to identify the
    // types and members involved, without letting a large file dominate the request.
    private const int MaxSnippetLength = 2000;

    public static async Task<string> RunAsync(
        string? hookInputJson,
        IServiceProvider services,
        TextWriter diagnostics,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = services.GetRequiredService<AgentRecallOptions>();
            if (!options.HookEnabled)
            {
                return string.Empty;
            }

            if (!TryReadPayload(hookInputJson, out var toolName, out var toolInput, out var cwd))
            {
                return string.Empty;
            }

            if (!FileMutatingTools.Contains(toolName))
            {
                return string.Empty;
            }

            var filePath = ReadFilePath(toolInput);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return string.Empty;
            }

            var repository = RepositoryName(cwd);
            var snippet = ExtractCode(toolName, toolInput);
            var request = new ContextRequest
            {
                // The recall query describes the write: the file name anchors domain
                // matching, and the code snippet lets semantic search see the types and
                // members involved (e.g. "class LoginService").
                Task = BuildTaskText(filePath, snippet),
                ScopeLevel = repository is null ? null : ScopeLevel.Repository,
                ScopeValue = repository,
                FileNames = [filePath],
                Limit = options.HookMaxRules,
                IncludePending = options.HookIncludePending,
                // Rules surfaced to the agent count as retrievals for learning reports.
                RecordUsage = true,
            };

            await using var scope = services.CreateAsyncScope();
            await HookDatabaseGuard.EnsureInitializedAsync(scope.ServiceProvider, cancellationToken)
                .ConfigureAwait(false);

            var context = scope.ServiceProvider.GetRequiredService<IContextInjectionService>();
            var result = await context.BuildContextAsync(request, cancellationToken).ConfigureAwait(false);

            var formatted = HookContextFormatter.Format(result);
            if (string.IsNullOrEmpty(formatted))
            {
                // No rule was relevant to this file: nothing to inject, no activity
                // recorded (a per-write hook must never spam on a no-op).
                return string.Empty;
            }

            // Stamp the turn id off the file path so the same file's writes correlate,
            // and so this retrieval can join the turn's capture in the activity log.
            var recorder = scope.ServiceProvider.GetRequiredService<IActivityRecorder>();
            var turnId = TurnCorrelation.Compute(cwd, filePath);
            var notice = ActivityNoticeFactory.ForContextFetched(result, "pretooluse");
            if (notice is not null)
            {
                await recorder.RecordAsync(notice with { TurnId = turnId }, cancellationToken).ConfigureAwait(false);
            }

            var conflictNotice = ActivityNoticeFactory.ForConflictResolved(result.Conflicts, "pretooluse");
            if (conflictNotice is not null)
            {
                await recorder.RecordAsync(conflictNotice with { TurnId = turnId }, cancellationToken).ConfigureAwait(false);
            }

            var compact = notice is null
                ? null
                : ActivityNoticeRenderer.RenderCompact(notice, options.ResolvedHookNoticeLevel);

            return string.IsNullOrEmpty(compact) ? formatted : compact + "\n\n" + formatted;
        }
        catch (Exception ex)
        {
            // Never block the write; surface the failure on stderr only.
            diagnostics.WriteLine($"[agentrecall] pre-tool-use hook skipped: {ex.Message}");
            return string.Empty;
        }
    }

    private static string BuildTaskText(string filePath, string? snippet)
    {
        var fileName = Path.GetFileName(filePath);
        return string.IsNullOrWhiteSpace(snippet)
            ? $"Editing {fileName}"
            : $"Editing {fileName}\n{snippet}";
    }

    /// <summary>
    /// Reads the code the tool is about to write. Tolerant of both the field names this
    /// Claude Code build uses (<c>content</c> for Write, <c>new_string</c> for Edit, an
    /// <c>edits</c> array for MultiEdit) and the alternates documented for the hook payload
    /// (<c>file_text</c>, <c>new_text</c>) — whichever the running host actually sends. The
    /// result is bounded to keep the recall query small.
    /// </summary>
    private static string? ExtractCode(string toolName, JsonObject? toolInput)
    {
        if (toolInput is null)
        {
            return null;
        }

        // A whole-file write carries the new contents directly.
        var whole = ReadString(toolInput["content"]) ?? ReadString(toolInput["file_text"]);
        if (!string.IsNullOrWhiteSpace(whole))
        {
            return Truncate(whole, MaxSnippetLength);
        }

        // An edits array (MultiEdit, or an Edit represented as edits) carries one or more
        // replacement strings; concatenate them so the recall query sees all new code.
        var joined = JoinEditStrings(toolInput["edits"]);
        if (!string.IsNullOrWhiteSpace(joined))
        {
            return Truncate(joined, MaxSnippetLength);
        }

        // A single-replacement Edit.
        var single = ReadString(toolInput["new_string"]) ?? ReadString(toolInput["new_text"]);
        return Truncate(single, MaxSnippetLength);
    }

    private static string? JoinEditStrings(JsonNode? edits)
    {
        if (edits is not JsonArray array)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var edit in array)
        {
            var obj = edit as JsonObject;
            var text = ReadString(obj?["new_string"]) ?? ReadString(obj?["new_text"]);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(text);
            if (sb.Length >= MaxSnippetLength)
            {
                break;
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// Reads the target file path, tolerant of both a top-level <c>file_path</c> (Write,
    /// Edit, MultiEdit) and one nested on the first entry of an <c>edits</c> array (the
    /// documented MultiEdit payload shape).
    /// </summary>
    private static string? ReadFilePath(JsonObject? toolInput)
    {
        if (toolInput is null)
        {
            return null;
        }

        var top = ReadString(toolInput["file_path"]);
        if (!string.IsNullOrWhiteSpace(top))
        {
            return top;
        }

        if (toolInput["edits"] is JsonArray { Count: > 0 } edits)
        {
            return ReadString((edits[0] as JsonObject)?["file_path"]);
        }

        return null;
    }

    private static bool TryReadPayload(string? json, out string toolName, out JsonObject? toolInput, out string? cwd)
    {
        toolName = string.Empty;
        toolInput = null;
        cwd = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (root is not JsonObject obj)
        {
            return false;
        }

        toolName = ReadString(obj["tool_name"]) ?? string.Empty;
        toolInput = obj["tool_input"] as JsonObject;
        cwd = ReadString(obj["cwd"]);
        return !string.IsNullOrEmpty(toolName);
    }

    /// <summary>Repository name = the nearest ancestor with a .git, else the cwd's name.</summary>
    private static string? RepositoryName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return null;
        }

        try
        {
            var dir = new DirectoryInfo(cwd);
            for (var current = dir; current is not null; current = current.Parent)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                {
                    return current.Name;
                }
            }

            return dir.Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= max ? value : value[..max];
    }
}
