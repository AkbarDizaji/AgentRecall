using AgentRecall.Cli.Hooks;
using AgentRecall.Core.Domain;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for the Stop-hook / finalize-turn payload parser. It turns the JSON a host
/// sends on stdin into a <see cref="Core.Finalization.TurnFinalizationInput"/>, resolving
/// the latest user/assistant text from inline fields, an inline transcript, or a
/// referenced transcript file, and deriving the repository scope from the working
/// directory. It is deliberately tolerant: malformed or empty input returns null rather
/// than throwing, so the hook never blocks Claude Code. Everything runs offline against
/// throwaway temp files.
/// </summary>
public class TurnPayloadTests
{
    private static readonly StringWriter Discard = new();

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentrecall-payload-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyOrWhitespace_ReturnsNull(string? payload)
    {
        Assert.Null(TurnPayload.Parse(payload, Discard));
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsNullAndReportsDiagnostic()
    {
        var diagnostics = new StringWriter();

        var result = TurnPayload.Parse("{ this is not valid json", diagnostics);

        Assert.Null(result);
        Assert.Contains("malformed payload", diagnostics.ToString());
    }

    [Fact]
    public void Parse_JsonNullLiteral_ReturnsNull()
    {
        Assert.Null(TurnPayload.Parse("null", Discard));
    }

    [Fact]
    public void Parse_InlinePromptAndResponse_AreUsedDirectly()
    {
        var payload = """
        {
          "prompt": "How do I add a migration?",
          "assistant_response": "Run dotnet ef migrations add.",
          "source": "manual",
          "accepted": true
        }
        """;

        var result = TurnPayload.Parse(payload, Discard);

        Assert.NotNull(result);
        Assert.Equal("How do I add a migration?", result!.Prompt);
        Assert.Equal("Run dotnet ef migrations add.", result.AssistantResponse);
        Assert.Equal("manual", result.Source);
        Assert.Equal(true, result.Accepted);
        Assert.Null(result.RawTranscript);
    }

    [Fact]
    public void Parse_MissingSource_DefaultsToStopHook()
    {
        var result = TurnPayload.Parse("""{ "prompt": "hi" }""", Discard);

        Assert.NotNull(result);
        Assert.Equal("stop_hook", result!.Source);
        Assert.Null(result.Accepted);
    }

    [Fact]
    public void Parse_InlineTranscript_ResolvesLastUserAndAssistantText()
    {
        var transcript = string.Join("\n",
            """{ "type": "user", "message": { "content": "first question" } }""",
            """{ "type": "assistant", "message": { "content": "first answer" } }""",
            """{ "type": "user", "message": { "content": "second question" } }""",
            """{ "type": "assistant", "message": { "content": "second answer" } }""");

        // Embed the transcript as a JSON string value.
        var payload = System.Text.Json.JsonSerializer.Serialize(new { transcript });

        var result = TurnPayload.Parse(payload, Discard);

        Assert.NotNull(result);
        Assert.Equal("second question", result!.Prompt);
        Assert.Equal("second answer", result.AssistantResponse);
        Assert.Equal(transcript, result.RawTranscript);
    }

    [Fact]
    public void Parse_TranscriptWithContentBlocks_JoinsTextBlocksAndSkipsNonText()
    {
        // The assistant message uses the content-blocks shape with a tool_use block
        // interleaved; only the text blocks are collected, joined by newlines.
        var assistantLine =
            """{ "type": "assistant", "message": { "content": [ { "type": "text", "text": "line one" }, { "type": "tool_use", "name": "x" }, { "type": "text", "text": "line two" } ] } }""";
        var transcript = string.Join("\n",
            """{ "type": "user", "message": { "content": [ { "type": "text", "text": "the question" } ] } }""",
            assistantLine);

        var payload = System.Text.Json.JsonSerializer.Serialize(new { transcript });

        var result = TurnPayload.Parse(payload, Discard);

        Assert.NotNull(result);
        Assert.Equal("the question", result!.Prompt);
        Assert.Equal("line one\nline two", result.AssistantResponse);
    }

    [Fact]
    public void Parse_TranscriptWithBlankAndMalformedLines_IgnoresThem()
    {
        var transcript = string.Join("\n",
            "",
            "not json at all",
            """{ "type": "system", "message": { "content": "ignored" } }""",
            """{ "type": "user", "message": { "content": "kept" } }""");

        var payload = System.Text.Json.JsonSerializer.Serialize(new { transcript });

        var result = TurnPayload.Parse(payload, Discard);

        Assert.NotNull(result);
        Assert.Equal("kept", result!.Prompt);
        Assert.Null(result.AssistantResponse);
    }

    [Fact]
    public void Parse_TranscriptPath_ReadsAndParsesReferencedFile()
    {
        var dir = NewTempDir();
        try
        {
            var transcriptPath = Path.Combine(dir, "transcript.jsonl");
            File.WriteAllText(transcriptPath, string.Join("\n",
                """{ "type": "user", "message": { "content": "from file" } }""",
                """{ "type": "assistant", "message": { "content": "answer from file" } }"""));

            var payload = System.Text.Json.JsonSerializer.Serialize(new { transcript_path = transcriptPath });

            var result = TurnPayload.Parse(payload, Discard);

            Assert.NotNull(result);
            Assert.Equal("from file", result!.Prompt);
            Assert.Equal("answer from file", result.AssistantResponse);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Parse_MissingTranscriptPath_ReturnsInputWithoutText()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(
            new { transcript_path = "/no/such/transcript/file.jsonl" });

        var result = TurnPayload.Parse(payload, Discard);

        Assert.NotNull(result);
        Assert.Null(result!.Prompt);
        Assert.Null(result.AssistantResponse);
    }

    [Fact]
    public void Parse_CwdInsideGitRepo_DerivesRepositoryScope()
    {
        var dir = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            var payload = System.Text.Json.JsonSerializer.Serialize(new { cwd = dir, prompt = "hi" });

            var result = TurnPayload.Parse(payload, Discard);

            Assert.NotNull(result);
            Assert.Equal(ScopeLevel.Repository, result!.ScopeLevel);
            Assert.Equal(new DirectoryInfo(dir).Name, result.ScopeValue);
            Assert.Equal(dir, result.Cwd);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- Judgment parsing -----------------------------------------------------

    [Fact]
    public void Parse_ValidJudgment_IsRead()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            prompt = "a turn",
            cwd = "/repo/project",
            judgment = new
            {
                decision = "Capture",
                memory_type = "EngineeringLesson",
                confidence = 0.9,
                capture_reason = "ObservedAgentFailure",
                normalized_rule = new
                {
                    title = "t",
                    condition = "when x",
                    action = "do y",
                    because = "z",
                    scope = "project",
                    tags = new[] { "a", "b" },
                },
            },
        });

        var verdict = TurnPayload.Parse(payload, Discard)!.SuppliedJudgment;

        Assert.NotNull(verdict);
        Assert.Equal(Core.Capture.Judge.JudgeDecision.Capture, verdict!.Decision);
        Assert.Equal(0.9, verdict.Confidence);
        Assert.Equal("do y", verdict.NormalizedRule!.Action);
        Assert.Equal(2, verdict.NormalizedRule.Tags.Count);
    }

    [Fact]
    public void Parse_NoJudgment_LeavesVerdictNull()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { prompt = "a turn", cwd = "/repo/project" });
        Assert.Null(TurnPayload.Parse(payload, Discard)!.SuppliedJudgment);
    }

    [Fact]
    public void Parse_UnknownDecisionEnum_LeavesVerdictNull()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            prompt = "a turn",
            cwd = "/repo/project",
            judgment = new { decision = "Bogus", capture_reason = "ObservedAgentFailure", confidence = 0.9 },
        });

        // A malformed verdict parses to null (judge unavailable → skip), never throwing.
        Assert.Null(TurnPayload.Parse(payload, Discard)!.SuppliedJudgment);
    }

    [Fact]
    public void Parse_OversizedJudgment_IsIgnored()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            prompt = "a turn",
            cwd = "/repo/project",
            judgment = new
            {
                decision = "Capture",
                capture_reason = "ObservedAgentFailure",
                confidence = 0.9,
                evidence = new string('x', 30000),
            },
        });

        Assert.Null(TurnPayload.Parse(payload, Discard)!.SuppliedJudgment);
    }
}
