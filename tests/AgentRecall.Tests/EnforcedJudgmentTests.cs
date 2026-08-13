using System.Text.Json.Nodes;
using AgentRecall.Cli;
using AgentRecall.Cli.Mcp.Tools;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture.Judge;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// The enforced-judgment flow: a substantive turn nobody judged does not finish. The Stop hook
/// returns Claude Code's block response asking the session model — the judge — for its verdict, the
/// turn resumes and submits one through <c>submit_capture_judgment</c>, and the next Stop finalizes
/// from it. Covers the block, both verdict outcomes (capture and rejection), the loop guard, the
/// unchanged non-hook surface, and the guarantee that a silent model never triggers a keyword
/// decision instead. Everything runs offline against a throwaway database.
/// </summary>
[Collection("ConsoleStdin")]
public class EnforcedJudgmentTests
{
    // Long enough to clear the substantive-turn size floor without carrying any keyword signal.
    private const string Prompt = "Rework the retry policy in the importer so a transient failure is retried once.";

    private const string Response =
        "Reworked the importer's retry policy to retry a transient failure once, then surface it. " +
        "Adjusted the surrounding tests and confirmed the suite passes locally before finishing up here.";

    private static async Task<TestDatabase> NewDbAsync(Action<Core.Configuration.AgentRecallOptions>? configure = null)
    {
        var db = new TestDatabase(configure);
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        return db;
    }

    private static string Payload(string? judgment = null, bool stopHookActive = false, string session = "chat-1")
    {
        var extra = judgment is null ? string.Empty : $",\n  \"judgment\": {judgment}";
        var resumed = stopHookActive ? ",\n  \"stop_hook_active\": true" : string.Empty;
        return $$"""
        {
          "cwd": "/repo/importer",
          "session_id": "{{session}}",
          "prompt": "{{Prompt}}",
          "assistant_response": "{{Response}}"{{resumed}}{{extra}}
        }
        """;
    }

    private static async Task<(int Code, string Output)> RunAsync(TestDatabase db, string stdin, params string[] args)
    {
        var originalIn = Console.In;
        var writer = new StringWriter();
        try
        {
            Console.SetIn(new StringReader(stdin));
            var code = await CommandRouter.RunAsync(args, db.Services, writer);
            return (code, writer.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    private static async Task<JsonNode> SubmitAsync(TestDatabase db, JsonObject arguments)
    {
        await using var scope = db.CreateScope();
        return await new SubmitCaptureJudgmentTool().InvokeAsync(arguments, scope.ServiceProvider, default);
    }

    private static JsonObject CaptureArgs() => new()
    {
        ["decision"] = "Capture",
        ["memory_type"] = "EngineeringLesson",
        ["capture_reason"] = "ObservedAgentFailure",
        ["confidence"] = 0.92,
        ["session_id"] = "chat-1",
        ["normalized_rule"] = new JsonObject
        {
            ["title"] = "Retry transient import failures once",
            ["condition"] = "when a batch importer hits a transient failure",
            ["action"] = "retry once, then surface the failure with its cause",
            ["because"] = "a silent infinite retry hides the real fault and stalls the import",
            ["scope"] = "importer",
        },
    };

    private static JsonObject RejectArgs() => new()
    {
        ["decision"] = "Skip",
        ["memory_type"] = "NotMemory",
        ["capture_reason"] = "NotReusable",
        ["confidence"] = 0.8,
        ["session_id"] = "chat-1",
        ["why_not_saved"] = "One-off cleanup with nothing reusable behind it.",
    };

    private static async Task<IReadOnlyList<RecallRule>> Rules(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync();
    }

    private static async Task<IReadOnlyList<TurnFinalization>> Finalizations(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ITurnFinalizationRepository>().ListAsync();
    }

    private static async Task<IReadOnlyList<TurnJudgmentRequest>> Requests(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ITurnJudgmentRequestRepository>().ListAsync();
    }

    // 1. A judgment on the payload finalizes straight away — no block, no request recorded.
    [Fact]
    public async Task SuppliedJudgment_FinalizesWithoutBlocking()
    {
        await using var db = await NewDbAsync();

        var judgment = """
        {
          "decision": "Skip",
          "memory_type": "NotMemory",
          "capture_reason": "NotMemory",
          "confidence": 0.9,
          "why_not_saved": "ordinary work"
        }
        """;

        var (code, output) = await RunAsync(db, Payload(judgment), "finalize-turn", "--hook");

        Assert.Equal(0, code);
        Assert.DoesNotContain("\"decision\":\"block\"", output);
        Assert.Empty(await Requests(db));

        var stored = Assert.Single(await Finalizations(db));
        Assert.Equal(TurnFinalizer.JudgeDecisionSource, stored.DecisionSource);
        Assert.Equal(nameof(JudgeDecision.Skip), stored.JudgeDecision);
    }

    // 2. No judgment on the first Stop emits the block response and records the ask.
    [Fact]
    public async Task NoJudgment_FirstStop_EmitsBlock()
    {
        await using var db = await NewDbAsync();

        var (code, output) = await RunAsync(db, Payload(), "finalize-turn", "--hook");

        Assert.Equal(0, code);
        var response = JsonNode.Parse(output.Trim())!.AsObject();
        Assert.Equal("block", response["decision"]!.GetValue<string>());
        Assert.Contains("submit_capture_judgment", response["reason"]!.GetValue<string>());
        Assert.Equal("Stop", response["hookSpecificOutput"]!["hookEventName"]!.GetValue<string>());

        // The turn is not finalized yet: the ask is recorded instead, so the verdict can answer it.
        Assert.Empty(await Finalizations(db));
        var request = Assert.Single(await Requests(db));
        Assert.Equal(JudgmentRequestStatus.Outstanding, request.Status);
        Assert.Equal(1, request.Attempts);
        Assert.Equal(Prompt, request.Prompt);
    }

    // 3. The resumed turn submits a capture verdict: the rule is stored and the request resolved.
    [Fact]
    public async Task ResumedTurn_SubmitsCapture_FinalizesAndCaptures()
    {
        await using var db = await NewDbAsync();
        await RunAsync(db, Payload(), "finalize-turn", "--hook");

        var result = (await SubmitAsync(db, CaptureArgs())).AsObject();

        Assert.True(result["submitted"]!.GetValue<bool>());
        Assert.Equal(nameof(JudgeDecision.Capture), result["decision"]!.GetValue<string>());
        Assert.Equal(TurnFinalizer.JudgeDecisionSource, result["decision_source"]!.GetValue<string>());

        var rule = Assert.Single(await Rules(db));
        Assert.Contains("retry once", rule.RuleText);

        var request = Assert.Single(await Requests(db));
        Assert.Equal(JudgmentRequestStatus.Resolved, request.Status);
        Assert.Equal(nameof(JudgeDecision.Capture), request.ResolvedDecision);
        Assert.NotNull(request.FinalizationId);
    }

    // 4. A rejection is a real verdict: the turn finalizes, the request closes, nothing is stored.
    [Fact]
    public async Task ResumedTurn_SubmitsRejection_FinalizesWithoutCapturing()
    {
        await using var db = await NewDbAsync();
        await RunAsync(db, Payload(), "finalize-turn", "--hook");

        var result = (await SubmitAsync(db, RejectArgs())).AsObject();

        Assert.True(result["submitted"]!.GetValue<bool>());
        Assert.Empty(await Rules(db));

        var stored = Assert.Single(await Finalizations(db));
        Assert.Equal(TurnFinalizer.JudgeDecisionSource, stored.DecisionSource);
        Assert.Equal(nameof(JudgeDecision.Skip), stored.JudgeDecision);
        Assert.Equal(JudgmentRequestStatus.Resolved, (await Requests(db)).Single().Status);
    }

    // 5. The next Stop after a verdict lets the turn finish, and files no second record for it —
    // even though the resumed turn says more and therefore hashes differently.
    [Fact]
    public async Task StopAfterSubmission_DoesNotBlockAndDoesNotDuplicate()
    {
        await using var db = await NewDbAsync();
        await RunAsync(db, Payload(), "finalize-turn", "--hook");
        await SubmitAsync(db, CaptureArgs());

        var resumedPayload = $$"""
        {
          "cwd": "/repo/importer",
          "session_id": "chat-1",
          "prompt": "{{Prompt}}",
          "assistant_response": "{{Response}} Then submitted the capture judgment as asked."
        }
        """;

        var (code, output) = await RunAsync(db, resumedPayload, "finalize-turn", "--hook");

        Assert.Equal(0, code);
        Assert.DoesNotContain("\"decision\":\"block\"", output);
        Assert.Single(await Finalizations(db));
        Assert.Single(await Rules(db));
    }

    // 6. The loop guard: a second unjudged Stop for the same turn finalizes instead of blocking
    // again, and says so — asked and unanswered, not "never judged".
    [Fact]
    public async Task SecondUnjudgedStop_DoesNotBlockAgain()
    {
        await using var db = await NewDbAsync();
        var (_, first) = await RunAsync(db, Payload(), "finalize-turn", "--hook");
        Assert.Contains("\"decision\":\"block\"", first);

        var (code, second) = await RunAsync(db, Payload(), "finalize-turn", "--hook");

        Assert.Equal(0, code);
        Assert.DoesNotContain("\"decision\":\"block\"", second);

        var stored = Assert.Single(await Finalizations(db));
        Assert.Equal(TurnFinalizer.JudgmentRetryExhaustedSource, stored.DecisionSource);
        Assert.Contains("resumed without one", stored.SkippedReasons);
        Assert.Equal(JudgmentRequestStatus.Abandoned, (await Requests(db)).Single().Status);
        Assert.Empty(await Rules(db));
    }

    // 6b. The host's own resumption signal, when a host sends one, also stops a second ask.
    [Fact]
    public async Task StopHookActive_DoesNotBlock()
    {
        await using var db = await NewDbAsync();

        var (code, output) = await RunAsync(db, Payload(stopHookActive: true), "finalize-turn", "--hook");

        Assert.Equal(0, code);
        Assert.DoesNotContain("\"decision\":\"block\"", output);
        Assert.Empty(await Requests(db));
        Assert.Equal(TurnFinalizer.JudgmentRetryExhaustedSource, (await Finalizations(db)).Single().DecisionSource);
    }

    // 7. A malformed verdict is rejected outright: nothing is stored, and the request stays open
    // rather than being closed by a verdict AgentRecall could not read.
    [Fact]
    public async Task MalformedSubmission_IsRejectedSafely()
    {
        await using var db = await NewDbAsync();
        await RunAsync(db, Payload(), "finalize-turn", "--hook");

        var result = (await SubmitAsync(db, new JsonObject
        {
            ["decision"] = "definitely-remember-this",
            ["capture_reason"] = "because-i-said-so",
            ["session_id"] = "chat-1",
        })).AsObject();

        Assert.False(result["submitted"]!.GetValue<bool>());
        Assert.Contains("decision", result["reason"]!.GetValue<string>());
        Assert.Empty(await Rules(db));
        Assert.Empty(await Finalizations(db));
        Assert.Equal(JudgmentRequestStatus.Outstanding, (await Requests(db)).Single().Status);
    }

    // 8. The non-hook surface is unchanged: it never blocks, and an unjudged turn is recorded as
    // one nobody judged — distinct from a rejection, and never keyword-captured.
    [Fact]
    public async Task NonHookFinalizeTurn_NeverBlocksAndNeverFallsBackToKeywords()
    {
        await using var db = await NewDbAsync();

        var (code, output) = await RunAsync(db, Payload(), "finalize-turn");

        Assert.Equal(0, code);
        Assert.DoesNotContain("\"decision\":\"block\"", output);
        Assert.Empty(await Requests(db));
        Assert.Empty(await Rules(db));

        var stored = Assert.Single(await Finalizations(db));
        Assert.Equal(TurnFinalizer.NoJudgmentSuppliedSource, stored.DecisionSource);
        Assert.Contains("No semantic capture judgment was supplied", stored.SkippedReasons);
        Assert.DoesNotContain("unavailable", stored.SkippedReasons);
    }

    // 8b. Enforcement Off restores the pre-enforcement Stop behaviour exactly.
    [Fact]
    public async Task EnforcementOff_FinalizesUnjudgedWithoutBlocking()
    {
        await using var db = await NewDbAsync(o => o.JudgmentEnforcementMode = nameof(JudgmentEnforcementMode.Off));

        var (code, output) = await RunAsync(db, Payload(), "finalize-turn", "--hook");

        Assert.Equal(0, code);
        Assert.DoesNotContain("\"decision\":\"block\"", output);
        Assert.Empty(await Requests(db));
        Assert.Equal(TurnFinalizer.NoJudgmentSuppliedSource, (await Finalizations(db)).Single().DecisionSource);
    }

    // 8c. A turn below the structural size floor is not worth asking about, so it is not blocked.
    [Fact]
    public async Task TrivialTurn_IsNotBlocked()
    {
        await using var db = await NewDbAsync();

        const string trivial = """
        {
          "cwd": "/repo/importer",
          "session_id": "chat-1",
          "prompt": "thanks",
          "assistant_response": "No problem."
        }
        """;

        var (code, output) = await RunAsync(db, trivial, "finalize-turn", "--hook");

        Assert.Equal(0, code);
        Assert.DoesNotContain("\"decision\":\"block\"", output);
        Assert.Empty(await Requests(db));
    }

    // 9. Both verdict outcomes persist the judge as the decision source with its confidence, so
    // capture-status can tell a rejection from an unjudged turn.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SubmittedVerdict_PersistsDecisionSourceAndConfidence(bool capture)
    {
        await using var db = await NewDbAsync();
        await RunAsync(db, Payload(), "finalize-turn", "--hook");

        await SubmitAsync(db, capture ? CaptureArgs() : RejectArgs());

        var stored = Assert.Single(await Finalizations(db));
        Assert.Equal(TurnFinalizer.JudgeDecisionSource, stored.DecisionSource);
        Assert.Equal(capture ? nameof(JudgeDecision.Capture) : nameof(JudgeDecision.Skip), stored.JudgeDecision);
        Assert.Equal(capture ? 0.92 : 0.8, stored.JudgeConfidence, 3);
        Assert.Equal(
            capture ? nameof(JudgeCaptureReason.ObservedAgentFailure) : nameof(JudgeCaptureReason.NotReusable),
            stored.JudgeCaptureReason);
    }

    // While a judgment is outstanding the status surfaces say so, instead of answering with the
    // previous turn's decision.
    [Fact]
    public async Task CaptureStatus_ReportsOutstandingJudgment()
    {
        await using var db = await NewDbAsync();
        await RunAsync(db, Payload(), "finalize-turn", "--hook");

        var (code, output) = await RunAsync(db, string.Empty, "capture-status", "--last-turn");
        Assert.Equal(0, code);
        Assert.Contains("still waiting", output);

        await using var scope = db.CreateScope();
        var status = (await new CaptureStatusTool().InvokeAsync(
            new JsonObject { ["session_id"] = "chat-1" }, scope.ServiceProvider, default)).AsObject();
        Assert.True(status["awaiting_judgment"]!.GetValue<bool>());
    }

    // A verdict volunteered with no outstanding request still finalizes the turn it names.
    [Fact]
    public async Task UnpromptedSubmission_FinalizesTheNamedTurn()
    {
        await using var db = await NewDbAsync();

        var args = CaptureArgs();
        args["prompt"] = Prompt;
        args["assistant_response"] = Response;
        args["cwd"] = "/repo/importer";

        var result = (await SubmitAsync(db, args)).AsObject();

        Assert.True(result["submitted"]!.GetValue<bool>());
        Assert.True(result["was_unprompted"]!.GetValue<bool>());
        Assert.Single(await Rules(db));
    }

    // Enforcement fails open: if the gate cannot read its own state it must not block, or a turn
    // could be blocked repeatedly with no record of the ask. The turn finalizes unjudged and the
    // failure is recorded, so it stays distinguishable from an ordinary unjudged turn.
    [Fact]
    public async Task EnforcementFailure_FailsOpenAndRecordsWhy()
    {
        await using var db = new TestDatabase(
            configure: null,
            configureServices: s => s.AddScoped<ITurnJudgmentRequestRepository, ThrowingJudgmentRequestRepository>());
        await using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        }

        var (code, output) = await RunAsync(db, Payload(), "finalize-turn", "--hook");

        Assert.Equal(0, code);
        Assert.DoesNotContain("\"decision\":\"block\"", output);
        Assert.Equal(TurnFinalizer.NoJudgmentSuppliedSource, (await Finalizations(db)).Single().DecisionSource);

        await using var check = db.CreateScope();
        var activities = await check.ServiceProvider.GetRequiredService<IAgentRecallActivityRepository>().ListAsync();
        Assert.Contains(activities, a => a.Summary.Contains("could not enforce", StringComparison.Ordinal));
    }

    /// <summary>A request store whose reads fail, standing in for a broken/locked database.</summary>
    private sealed class ThrowingJudgmentRequestRepository : ITurnJudgmentRequestRepository
    {
        public Task<TurnJudgmentRequest?> FindOutstandingAsync(
            string? sessionId, string? cwd, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("request store unavailable");

        public Task<TurnJudgmentRequest?> FindByTurnAsync(string turnId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("request store unavailable");

        public Task<TurnJudgmentRequest> AddAsync(TurnJudgmentRequest entity, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("request store unavailable");

        public Task<TurnJudgmentRequest?> GetAsync(int id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("request store unavailable");

        public Task<IReadOnlyList<TurnJudgmentRequest>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("request store unavailable");

        public Task<TurnJudgmentRequest> UpdateAsync(TurnJudgmentRequest entity, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("request store unavailable");

        public Task AddRangeAsync(IReadOnlyCollection<TurnJudgmentRequest> entities, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("request store unavailable");

        public Task UpdateRangeAsync(IReadOnlyCollection<TurnJudgmentRequest> entities, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("request store unavailable");

        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("request store unavailable");
    }

    // A verdict with no request and no turn to attach it to is refused, not guessed at.
    [Fact]
    public async Task UnpromptedSubmission_WithNoTurn_IsRefused()
    {
        await using var db = await NewDbAsync();

        var result = (await SubmitAsync(db, CaptureArgs())).AsObject();

        Assert.False(result["submitted"]!.GetValue<bool>());
        Assert.Contains("no prompt", result["reason"]!.GetValue<string>());
        Assert.Empty(await Rules(db));
    }
}
