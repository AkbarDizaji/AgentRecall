using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Finalization;

namespace AgentRecall.Cli.Hooks;

/// <summary>
/// Parses a Claude Code Stop-hook payload (or the equivalent JSON the
/// <c>finalize-turn</c> command reads on stdin) into a <see cref="TurnFinalizationInput"/>.
/// It resolves the user and assistant text from inline fields or the referenced
/// transcript, and derives the repository scope from the working directory — keeping
/// all file IO and host-shape knowledge in the CLI so the core finalizer stays pure.
///
/// Tolerant of missing fields and malformed input: returns <c>null</c> rather than
/// throwing, so the hook never blocks Claude Code.
/// </summary>
public static class TurnPayload
{
    /// <summary>
    /// Parses the payload, or returns <c>null</c> when it is empty/unparseable. A
    /// successful parse may still carry empty prompt/response when the turn provided none.
    /// </summary>
    public static TurnFinalizationInput? Parse(string? payloadJson, TextWriter diagnostics)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payloadJson);
        }
        catch (JsonException ex)
        {
            diagnostics.WriteLine($"[agentrecall] finalize-turn: ignoring malformed payload: {ex.Message}");
            return null;
        }

        // A well-formed payload is a JSON object. Anything else (an array, a bare scalar)
        // carries no fields to read, so it is tolerated as "nothing to finalize".
        if (root is not JsonObject obj)
        {
            return null;
        }

        var cwd = NonEmpty(AsString(obj["cwd"])) ?? SafeCurrentDirectory();
        var source = NonEmpty(AsString(obj["source"])) ?? "stop_hook";
        var accepted = AsBool(obj["accepted"]);

        var (userText, assistantText, rawTranscript) = ResolveText(obj, diagnostics);
        var repository = RepositoryName(cwd);

        return new TurnFinalizationInput
        {
            Cwd = cwd,
            Prompt = userText,
            AssistantResponse = assistantText,
            Source = source,
            Accepted = accepted,
            ScopeLevel = repository is null ? ScopeLevel.Global : ScopeLevel.Repository,
            ScopeValue = repository,
            RawTranscript = rawTranscript,
        };
    }

    /// <summary>
    /// Resolves the latest user and assistant text, preferring inline fields, then an
    /// inline transcript string, then the referenced transcript file.
    /// </summary>
    private static (string? User, string? Assistant, string? Raw) ResolveText(JsonObject root, TextWriter diagnostics)
    {
        var inlinePrompt = NonEmpty(AsString(root["prompt"]));
        var inlineAssistant = NonEmpty(AsString(root["assistant_response"]));
        if (inlinePrompt is not null || inlineAssistant is not null)
        {
            return (inlinePrompt, inlineAssistant, null);
        }

        var inlineTranscript = NonEmpty(AsString(root["transcript"]));
        if (inlineTranscript is not null)
        {
            var (u, a) = ParseTranscriptText(inlineTranscript);
            return (u, a, inlineTranscript);
        }

        var transcriptPath = NonEmpty(AsString(root["transcript_path"]));
        if (transcriptPath is null || !File.Exists(transcriptPath))
        {
            return (null, null, null);
        }

        try
        {
            var (u, a) = ParseTranscriptLines(File.ReadLines(transcriptPath));
            return (u, a, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.WriteLine($"[agentrecall] finalize-turn: could not read transcript: {ex.Message}");
            return (null, null, null);
        }
    }

    /// <summary>Parses a JSONL transcript string into the last user and assistant text.</summary>
    private static (string? User, string? Assistant) ParseTranscriptText(string transcript) =>
        ParseTranscriptLines(transcript.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

    private static (string? User, string? Assistant) ParseTranscriptLines(IEnumerable<string> lines)
    {
        string? lastUser = null;
        string? lastAssistant = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? entry;
            try
            {
                entry = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            // A transcript line that isn't a JSON object carries no turn to read.
            if (entry is not JsonObject entryObject)
            {
                continue;
            }

            var type = AsString(entryObject["type"]);
            if (type is not ("user" or "assistant"))
            {
                continue;
            }

            var text = ExtractMessageText(entryObject["message"]);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (type == "user")
            {
                lastUser = text;
            }
            else
            {
                lastAssistant = text;
            }
        }

        return (lastUser, lastAssistant);
    }

    private static string? ExtractMessageText(JsonNode? message)
    {
        if (message is not JsonObject messageObject)
        {
            return null;
        }

        var content = messageObject["content"];
        if (content is null)
        {
            return null;
        }

        if (content is JsonValue value && value.TryGetValue<string>(out var direct))
        {
            return direct;
        }

        if (content is not JsonArray blocks)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            if (block is not JsonObject blockObject || AsString(blockObject["type"]) != "text")
            {
                continue;
            }

            var blockText = AsString(blockObject["text"]);
            if (!string.IsNullOrWhiteSpace(blockText))
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(blockText);
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
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

    private static string? SafeCurrentDirectory()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    // Reads a field only when it is actually a JSON string / bool. A host that sends the
    // wrong type (a number where a string is expected) is tolerated as a missing field
    // rather than throwing — the parser must never block the hook.
    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? AsBool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}
