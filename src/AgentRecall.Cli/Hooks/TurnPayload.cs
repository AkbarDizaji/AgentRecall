using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecall.Core.Capture.Judge;
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
        var sessionId = NonEmpty(AsString(obj["session_id"]));

        // Read as a courtesy, not as a dependency: `stop_hook_active` is not part of the documented
        // Stop payload, so the enforced-judgment loop guard is AgentRecall's own attempt counter.
        // When a host does send it, it only ever means "do not ask again on this turn".
        var hostResumed = AsBool(obj["stop_hook_active"]) ?? false;

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
            SessionId = sessionId,
            SuppliedJudgment = ParseJudgment(obj["judgment"], diagnostics),
            SuppliedDocOpportunity = ParseDocOpportunity(obj["doc_opportunity"], diagnostics),
            HostResumedTurn = hostResumed,
        };
    }

    /// <summary>
    /// Parses a judgment object on its own — the same verdict shape the Stop-hook payload carries,
    /// used by the <c>submit_capture_judgment</c> MCP tool so a verdict submitted mid-turn is read
    /// by exactly the code that reads one arriving on a payload. Returns <c>null</c> for anything
    /// missing, oversized, or malformed; the caller never substitutes a decision of its own.
    /// </summary>
    public static CaptureJudgeVerdict? ParseVerdict(JsonNode? judgment, TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return ParseJudgment(judgment, diagnostics);
    }

    // The semantic judge's verdict is produced by the host and arrives as a `judgment` object on
    // the payload. Parsing is tolerant: a missing/oversized/malformed judgment (including an
    // unrecognised enum value) yields null — the finalizer then treats the judgment as absent
    // and skips, never falling back to keyword capture.
    private const int JudgmentMaxLength = 20000;

    private static CaptureJudgeVerdict? ParseJudgment(JsonNode? node, TextWriter diagnostics)
    {
        if (node is not JsonObject judgment)
        {
            return null;
        }

        // Bound the supplied judgment so a hostile payload cannot blow up memory.
        if (judgment.ToJsonString().Length > JudgmentMaxLength)
        {
            diagnostics.WriteLine("[agentrecall] finalize-turn: ignoring oversized judgment.");
            return null;
        }

        // Enums are required and must be recognised; an unknown value is a malformed verdict.
        if (!TryParseEnum<JudgeDecision>(AsString(judgment["decision"]), out var decision) ||
            !TryParseEnum<JudgeCaptureReason>(AsString(judgment["capture_reason"]), out var reason))
        {
            return null;
        }

        // memory_type is only meaningful when a rule is stored; default it when absent so a
        // Skip verdict need not carry one.
        if (!TryParseEnum<JudgeMemoryType>(AsString(judgment["memory_type"]), out var memoryType, JudgeMemoryType.NotMemory))
        {
            return null;
        }

        return new CaptureJudgeVerdict
        {
            Decision = decision,
            MemoryType = memoryType,
            Confidence = AsDouble(judgment["confidence"]) ?? 0.0,
            CaptureReason = reason,
            TargetExistingRuleId = AsInt(judgment["target_existing_rule_id"]),
            NormalizedRule = ParseNormalizedRule(judgment["normalized_rule"]),
            Evidence = AsString(judgment["evidence"]),
            WhyNotSaved = AsString(judgment["why_not_saved"]),
            DedupeNotes = AsString(judgment["dedupe_notes"]),
        };
    }

    private static NormalizedRule? ParseNormalizedRule(JsonNode? node)
    {
        if (node is not JsonObject rule)
        {
            return null;
        }

        var tags = new List<string>();
        if (rule["tags"] is JsonArray tagArray)
        {
            foreach (var tag in tagArray)
            {
                var text = AsString(tag);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    tags.Add(text!);
                }
            }
        }

        return new NormalizedRule
        {
            Title = AsString(rule["title"]),
            Condition = AsString(rule["condition"]),
            Action = AsString(rule["action"]),
            Avoid = AsString(rule["avoid"]),
            Because = AsString(rule["because"]),
            Scope = AsString(rule["scope"]),
            AlwaysApply = AsBool(rule["always_apply"]) ?? false,
            Tags = tags,
        };
    }

    // The document-opportunity judge's verdict is produced by the host and arrives as a
    // `doc_opportunity` object on the payload, a sibling of `judgment`. Parsing follows the same
    // tolerant rules: missing/oversized/malformed (including an unrecognised enum value) yields
    // null — the caller then treats the judge as unavailable and offers nothing.
    private const int DocOpportunityMaxLength = 20000;

    private static DocOpportunityVerdict? ParseDocOpportunity(JsonNode? node, TextWriter diagnostics)
    {
        if (node is not JsonObject opportunity)
        {
            return null;
        }

        // Bound the supplied verdict so a hostile payload cannot blow up memory.
        if (opportunity.ToJsonString().Length > DocOpportunityMaxLength)
        {
            diagnostics.WriteLine("[agentrecall] finalize-turn: ignoring oversized doc_opportunity.");
            return null;
        }

        // decision is required and must be recognised; an unknown value is a malformed verdict.
        if (!TryParseEnum<DocOpportunityDecision>(AsString(opportunity["decision"]), out var decision))
        {
            return null;
        }

        // document_type only matters when offering; default it when absent so a Skip verdict
        // need not carry one.
        if (!TryParseEnum<DocumentType>(AsString(opportunity["document_type"]), out var documentType, DocumentType.Incident))
        {
            return null;
        }

        var keyPoints = new List<string>();
        if (opportunity["key_points"] is JsonArray keyPointArray)
        {
            foreach (var point in keyPointArray)
            {
                var text = AsString(point);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    keyPoints.Add(text!);
                }
            }
        }

        return new DocOpportunityVerdict
        {
            Decision = decision,
            DocumentType = documentType,
            Confidence = AsDouble(opportunity["confidence"]) ?? 0.0,
            SuggestedTitle = AsString(opportunity["suggested_title"]),
            Reason = AsString(opportunity["reason"]),
            KeyPoints = keyPoints,
            WhyNotOffered = AsString(opportunity["why_not_offered"]),
        };
    }

    // Parses a required enum. Returns false when the value is missing or unrecognised, unless a
    // fallback is supplied (used for the optional memory_type on a Skip verdict).
    private static bool TryParseEnum<TEnum>(string? value, out TEnum result, TEnum? fallback = null)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (fallback is { } f)
            {
                result = f;
                return true;
            }

            result = default;
            return false;
        }

        if (Enum.TryParse(value, ignoreCase: true, out result) && Enum.IsDefined(result))
        {
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>
    /// Resolves the latest user and assistant text, preferring inline fields, then an
    /// inline transcript string, then the referenced transcript file.
    /// </summary>
    private static (string? User, string? Assistant, string? Raw) ResolveText(JsonObject root, TextWriter diagnostics)
    {
        var inlinePrompt = NonEmpty(AsString(root["prompt"]));

        // `last_assistant_message` is the documented Stop-hook field for the turn's final assistant
        // text; prefer it over re-reading the transcript, and keep `assistant_response` for the
        // hand-written payloads the CLI path accepts.
        var inlineAssistant =
            NonEmpty(AsString(root["assistant_response"])) ??
            NonEmpty(AsString(root["last_assistant_message"]));
        if (inlinePrompt is not null || inlineAssistant is not null)
        {
            // A Stop payload can carry the assistant text but never the prompt, so fill the missing
            // half from the transcript rather than finalizing a half-empty turn.
            if (inlinePrompt is null || inlineAssistant is null)
            {
                var (fallbackUser, fallbackAssistant, raw) = ResolveFromTranscript(root, diagnostics);
                return (inlinePrompt ?? fallbackUser, inlineAssistant ?? fallbackAssistant, raw);
            }

            return (inlinePrompt, inlineAssistant, null);
        }

        return ResolveFromTranscript(root, diagnostics);
    }

    /// <summary>Reads the turn from an inline transcript string, else the referenced transcript file.</summary>
    private static (string? User, string? Assistant, string? Raw) ResolveFromTranscript(
        JsonObject root, TextWriter diagnostics)
    {
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

    /// <summary>
    /// Repository name = the nearest ancestor with a .git, else the cwd's name. Public because the
    /// <c>submit_capture_judgment</c> tool derives the same scope for a verdict submitted mid-turn,
    /// and a verdict must not land in a different scope than the Stop hook would have used.
    /// </summary>
    public static string? RepositoryScopeName(string? cwd) => RepositoryName(cwd);

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

    // Tolerant of numbers that don't round-trip as a JSON integer literal: some JSON encoders
    // (JavaScript's JSON.stringify, jq, etc.) always emit numeric fields as a double, so
    // target_existing_rule_id can arrive as 43.0 rather than 43. A whole-number double is still
    // a valid id; only fractional values or non-numeric strings are rejected.
    private static int? AsInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<double>(out var real) && !double.IsNaN(real) && !double.IsInfinity(real) &&
            real == Math.Floor(real) && real >= int.MinValue && real <= int.MaxValue)
        {
            return (int)real;
        }

        if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? AsDouble(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}
