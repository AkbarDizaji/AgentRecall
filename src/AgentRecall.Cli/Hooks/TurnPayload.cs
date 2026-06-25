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

        if (root is null)
        {
            return null;
        }

        var cwd = NonEmpty(root["cwd"]?.GetValue<string>()) ?? SafeCurrentDirectory();
        var source = NonEmpty(root["source"]?.GetValue<string>()) ?? "stop_hook";
        var accepted = root["accepted"]?.GetValue<bool>();

        var (userText, assistantText, rawTranscript) = ResolveText(root, diagnostics);
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
    private static (string? User, string? Assistant, string? Raw) ResolveText(JsonNode root, TextWriter diagnostics)
    {
        var inlinePrompt = NonEmpty(root["prompt"]?.GetValue<string>());
        var inlineAssistant = NonEmpty(root["assistant_response"]?.GetValue<string>());
        if (inlinePrompt is not null || inlineAssistant is not null)
        {
            return (inlinePrompt, inlineAssistant, null);
        }

        var inlineTranscript = NonEmpty(root["transcript"]?.GetValue<string>());
        if (inlineTranscript is not null)
        {
            var (u, a) = ParseTranscriptText(inlineTranscript);
            return (u, a, inlineTranscript);
        }

        var transcriptPath = NonEmpty(root["transcript_path"]?.GetValue<string>());
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

            var type = entry?["type"]?.GetValue<string>();
            if (type is not ("user" or "assistant"))
            {
                continue;
            }

            var text = ExtractMessageText(entry!["message"]);
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
        var content = message?["content"];
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
            if (block?["type"]?.GetValue<string>() != "text")
            {
                continue;
            }

            var blockText = block["text"]?.GetValue<string>();
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
}
