using System.Text.Json.Nodes;
using AgentRecall.Cli;
using AgentRecall.Cli.Mcp.Tools;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Capture.Judge;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// End-to-end tests for the semantic capture judge on the finalize-turn path: the host supplies
/// a verdict, AgentRecall validates it and persists the result, and there is never a
/// keyword-driven fallback. Covers the product contract (explicit save/do-not-save, reviewer and
/// observed-failure lessons, doc-only vs doc-backed, prose/off-topic/incidental-keyword skips,
/// reinforce/supersede, confidence bands, unavailable-judge, mode Off) plus the status surfaces.
/// </summary>
[Collection("ConsoleStdin")]
public class CaptureJudgeFinalizerTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static TurnFinalizationInput Turn(
        CaptureJudgeVerdict? judgment, string? prompt = "a turn", string? assistant = null, string? cwd = "/repo/project") =>
        new()
        {
            Prompt = prompt,
            AssistantResponse = assistant,
            Source = "stop_hook",
            Cwd = cwd,
            ScopeLevel = cwd is null ? ScopeLevel.Global : ScopeLevel.Repository,
            ScopeValue = cwd is null ? null : "project",
            SuppliedJudgment = judgment,
        };

    private static async Task<TurnFinalizationResult> Finalize(TestDatabase db, TurnFinalizationInput input)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ITurnFinalizer>().FinalizeAsync(input);
    }

    private static async Task<IReadOnlyList<RecallRule>> Rules(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync();
    }

    private static CaptureJudgeVerdict Capture(
        JudgeMemoryType memoryType, JudgeCaptureReason reason, double confidence = 0.9, NormalizedRule? rule = null) => new()
    {
        Decision = JudgeDecision.Capture,
        MemoryType = memoryType,
        CaptureReason = reason,
        Confidence = confidence,
        NormalizedRule = rule ?? JudgeVerdicts.Rule(),
    };

    // A. Explicit save captures a specific/narrow rule as active.
    [Fact]
    public async Task A_ExplicitSave_CapturesSpecificRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var narrow = JudgeVerdicts.Rule(
            title: "Persist paymentMethodId in EventRegistrationService",
            condition: "when implementing paid waitlist registration in this codebase",
            action: "attach/persist paymentMethodId through the existing card-save path, or stop collecting it",
            because: "validate-and-drop creates a false guarantee for later promotion charging",
            scope: "project");
        var result = await Finalize(db, Turn(Capture(
            JudgeMemoryType.EngineeringLesson, JudgeCaptureReason.ExplicitUserSave, 0.4, narrow)));

        Assert.Single(result.Captured);
        Assert.Equal(RuleStatus.Active, (await Rules(db)).Single().Status);
    }

    // B. Explicit save captures a communication preference at Global scope.
    [Fact]
    public async Task B_ExplicitSave_CapturesCommunicationPreferenceGlobally()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var pref = JudgeVerdicts.Rule(
            title: "Answer briefly", condition: "when answering the user",
            action: "answer briefly and simply, with examples when helpful",
            because: "the user asked for concise answers", scope: "global");
        var result = await Finalize(db, Turn(Capture(
            JudgeMemoryType.CommunicationPreference, JudgeCaptureReason.UserPreference, 0.9, pref)));

        var stored = Assert.Single(await Rules(db));
        Assert.Equal(RuleCategory.CommunicationPreference, stored.Category);
        Assert.Equal(ScopeLevel.Global, stored.ScopeLevel);
    }

    // C. Explicit do-not-save skips — no Active and no Pending rule.
    [Fact]
    public async Task C_ExplicitDoNotSave_Skips()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(JudgeVerdicts.Skip(
            JudgeCaptureReason.ExplicitUserDoNotSave, "the user asked not to save this")));

        Assert.Empty(result.Captured);
        Assert.Empty(result.Suggested);
        Assert.Empty(await Rules(db));
    }

    // D. A reviewer correction captures a repository convention with the review reason.
    [Fact]
    public async Task D_ReviewerCorrection_Captures()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(Capture(
            JudgeMemoryType.RepositoryConvention, JudgeCaptureReason.ReviewerCorrection)));

        var lesson = Assert.Single(result.Captured);
        var rule = (await Rules(db)).Single(r => r.Id == lesson.RuleId);
        Assert.Equal(CaptureReason.AcceptedReviewComment, rule.CaptureReason);
    }

    // E. An observed agent failure captures an engineering lesson.
    [Fact]
    public async Task E_ObservedFailure_CapturesEngineeringLesson()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(Capture(
            JudgeMemoryType.EngineeringLesson, JudgeCaptureReason.ObservedAgentFailure)));

        Assert.Equal(RuleCategory.EngineeringLesson, Assert.Single(result.Captured).Category);
    }

    // F. A skill/tool document read during the turn is not saved on its own.
    [Fact]
    public async Task F_SourceDocumentOnly_Skips()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(JudgeVerdicts.Skip(
            JudgeCaptureReason.SourceDocumentOnly, "documentation was read, no correction or save")));

        Assert.Empty(result.Captured);
        Assert.Empty(result.Suggested);
        Assert.Empty(await Rules(db));
    }

    // G. A documentation-backed correction is captured.
    [Fact]
    public async Task G_DocBackedCorrection_Captures()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(Capture(
            JudgeMemoryType.DocBackedCorrection, JudgeCaptureReason.DocBackedCorrection)));

        Assert.Single(result.Captured);
        Assert.Equal(RuleCategory.EngineeringLesson, (await Rules(db)).Single().Category);
    }

    // H. Assistant prose is skipped.
    [Fact]
    public async Task H_AssistantProse_Skips()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(JudgeVerdicts.Skip(
            JudgeCaptureReason.AssistantProse, "assistant meta commentary, not a rule")));

        Assert.Empty(await Rules(db));
    }

    // I + J. An off-topic sentence that merely mentions incidental keywords is not memory.
    [Theory]
    [InlineData("Off topic: in the registration modal we change the button according to validation.")]
    [InlineData("We touched auth, scope, and security wiring but nothing worth remembering.")]
    public async Task IJ_IncidentalKeywords_DoNotCapture(string prompt)
    {
        await using var db = new TestDatabase();
        await Init(db);

        // The judge (not a keyword rule) decides this is not memory.
        var result = await Finalize(db, Turn(JudgeVerdicts.Skip(JudgeCaptureReason.NotMemory, "off-topic"), prompt: prompt));

        Assert.Empty(result.Captured);
        Assert.Empty(result.Suggested);
        Assert.Empty(await Rules(db));
    }

    // K. A duplicate of a retrieved rule reinforces it instead of creating a new rule.
    [Fact]
    public async Task K_ReinforceExisting_RecordsNoNewRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        int existingId;
        await using (var scope = db.CreateScope())
        {
            var feedback = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
            var seeded = await feedback.AddAsync(new FeedbackInput
            {
                Task = "work",
                Feedback = "We do not mock DbContext directly.",
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "project",
                AutoApprove = true,
            });
            existingId = seeded.Rule!.Id;
        }

        var result = await Finalize(db, Turn(JudgeVerdicts.Reinforce(existingId)));

        Assert.Empty(result.Captured);
        Assert.Contains(existingId, result.Duplicates);
        Assert.Single(await Rules(db));
    }

    // L. The relevant existing rules are surfaced to the judge in its input.
    [Fact]
    public async Task L_JudgeInput_IncludesRetrievedRules()
    {
        var fake = new FakeCaptureJudge(JudgeVerdicts.Skip());
        await using var db = new TestDatabase(configure: null, s => s.AddSingleton<ICaptureJudge>(fake));
        await Init(db);

        int seededId;
        await using (var scope = db.CreateScope())
        {
            var feedback = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
            var seeded = await feedback.AddAsync(new FeedbackInput
            {
                Task = "database tests",
                Feedback = "When writing database tests, use a real SQLite context instead of mocking DbContext.",
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "project",
                AutoApprove = true,
            });
            seededId = seeded.Rule!.Id;
        }

        await Finalize(db, Turn(JudgeVerdicts.Skip(),
            assistant: "I refactored the database tests that mock DbContext and the SQLite context."));

        Assert.NotNull(fake.LastInput);
        Assert.Contains(fake.LastInput!.RelevantRules, r => r.Id == seededId);
    }

    // M. A mid-band verdict (a conflict the judge chose to suggest) parks a Pending rule.
    [Fact]
    public async Task M_Conflict_IsSuggestedNotAutoCaptured()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(JudgeVerdicts.Suggest()));

        Assert.Empty(result.Captured);
        Assert.Equal(RuleStatus.Pending, (await Rules(db)).Single().Status);
    }

    // N. A code fact is skipped unless the user explicitly saved it with a rationale.
    [Fact]
    public async Task N_CodeFact_SkipsUnlessExplicitSave()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var skipResult = await Finalize(db, Turn(Capture(
            JudgeMemoryType.CodeFact, JudgeCaptureReason.CodeFact, 0.95)));
        Assert.Empty(await Rules(db));
        Assert.Empty(skipResult.Captured);

        var saveResult = await Finalize(db, Turn(Capture(
            JudgeMemoryType.CodeFact, JudgeCaptureReason.ExplicitUserSave, 0.4), prompt: "different turn"));
        Assert.Single(saveResult.Captured);
    }

    // O + P. A narrow, non-universal project lesson is captured — specific is fine.
    [Fact]
    public async Task OP_NarrowScopedLesson_IsCaptured()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var narrow = JudgeVerdicts.Rule(
            title: "Waitlist paymentMethodId",
            condition: "when implementing paid waitlist registration in EventRegistrationService",
            action: "do not validate paymentMethodId and then drop it; attach/persist it or stop collecting it",
            because: "promotion charging later needs the token",
            scope: "project");
        var result = await Finalize(db, Turn(Capture(
            JudgeMemoryType.RepositoryConvention, JudgeCaptureReason.ObservedAgentFailure, 0.85, narrow)));

        Assert.Single(result.Captured);
    }

    // Q. Invalid judge JSON (unknown enum) skips with no keyword fallback.
    [Fact]
    public async Task Q_InvalidJudgeEnum_SkipsNoFallback()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var payload = new JsonObject
        {
            ["prompt"] = "When writing SQL use parameterized queries.",
            ["cwd"] = "/repo/project",
            ["judgment"] = new JsonObject
            {
                ["decision"] = "Bogus",
                ["memory_type"] = "EngineeringLesson",
                ["confidence"] = 0.99,
                ["capture_reason"] = "ObservedAgentFailure",
            },
        };

        var (code, _) = await RunCli(db, payload, "finalize-turn");
        Assert.Equal(0, code);
        Assert.Empty(await Rules(db)); // never keyword-captured despite "SQL"/"parameterized"
    }

    // R. A verdict missing a rationale downgrades to a Pending suggestion (validation policy).
    [Fact]
    public async Task R_MissingBecause_DowngradesToSuggestion()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var incomplete = JudgeVerdicts.Rule(because: "");
        var result = await Finalize(db, Turn(Capture(
            JudgeMemoryType.EngineeringLesson, JudgeCaptureReason.ObservedAgentFailure, 0.9, incomplete)));

        Assert.Empty(result.Captured);
        Assert.Equal(RuleStatus.Pending, (await Rules(db)).Single().Status);
    }

    // S/T/U. Confidence bands: >=0.80 captures, 0.55-0.79 suggests, <0.55 skips.
    [Theory]
    [InlineData(0.80, 1, 0)]
    [InlineData(0.60, 0, 1)]
    [InlineData(0.50, 0, 0)]
    public async Task STU_ConfidenceBands(double confidence, int capturedCount, int pendingCount)
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(Capture(
            JudgeMemoryType.EngineeringLesson, JudgeCaptureReason.RepositoryConvention, confidence)));

        Assert.Equal(capturedCount, result.Captured.Count);
        var pending = (await Rules(db)).Count(r => r.Status == RuleStatus.Pending);
        Assert.Equal(pendingCount, pending);
    }

    // V. SourceDocumentOnly creates no Pending rule.
    [Fact]
    public async Task V_SourceDocumentOnly_CreatesNoPending()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await Finalize(db, Turn(JudgeVerdicts.Skip(JudgeCaptureReason.SourceDocumentOnly, "doc read only")));

        Assert.DoesNotContain(await Rules(db), r => r.Status == RuleStatus.Pending);
    }

    // W. capture-status shows the semantic judge decision, reason, and confidence.
    [Fact]
    public async Task W_CaptureStatus_ShowsJudgeDecision()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Finalize(db, Turn(Capture(JudgeMemoryType.RepositoryConvention, JudgeCaptureReason.ReviewerCorrection)));

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["capture-status", "--last-turn"], db.Services, output);

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("Semantic capture judge", text, StringComparison.Ordinal);
        Assert.Contains("ReviewerCorrection", text, StringComparison.Ordinal);
    }

    // W(json) + MCP. The decision fields are on the JSON and MCP surfaces.
    [Fact]
    public async Task W_McpCaptureStatus_IncludesDecisionFields()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Finalize(db, Turn(Capture(JudgeMemoryType.EngineeringLesson, JudgeCaptureReason.ObservedAgentFailure)));

        await using var scope = db.CreateScope();
        var status = await new CaptureStatusTool().InvokeAsync(null, scope.ServiceProvider, CancellationToken.None);

        Assert.Equal("SemanticCaptureJudge", status["decision_source"]!.GetValue<string>());
        Assert.Equal("Capture", status["decision"]!.GetValue<string>());
        Assert.Equal("ObservedAgentFailure", status["capture_reason"]!.GetValue<string>());
    }

    // X. turn-summary shows the judge result (the captured rule surfaces).
    [Fact]
    public async Task X_TurnSummary_ShowsJudgeResult()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Finalize(db, Turn(Capture(JudgeMemoryType.EngineeringLesson, JudgeCaptureReason.ObservedAgentFailure)));

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["turn-summary", "--last", "--detailed"], db.Services, output);

        Assert.Equal(0, code);
        Assert.Contains("aptured", output.ToString(), StringComparison.Ordinal);
    }

    // Y. The hook stays non-blocking (exit 0) even for a skip verdict.
    [Fact]
    public async Task Y_Hook_RemainsNonBlocking()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var payload = new JsonObject
        {
            ["prompt"] = "a turn",
            ["cwd"] = "/repo/project",
            ["judgment"] = new JsonObject
            {
                ["decision"] = "Skip",
                ["confidence"] = 0.1,
                ["capture_reason"] = "NotMemory",
                ["why_not_saved"] = "nothing worth saving",
            },
        };

        var (code, _) = await RunCli(db, payload, "finalize-turn", "--hook");
        Assert.Equal(0, code);
    }

    // Z. When no judgment is supplied, the judge is unavailable and the turn is skipped —
    //    never keyword-captured, even for strongly imperative text.
    [Fact]
    public async Task Z_NoJudgment_SkipsWithUnavailableMessage()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(judgment: null,
            prompt: "Always use parameterized queries and never mock DbContext directly."));

        Assert.Empty(result.Captured);
        Assert.Empty(result.Suggested);
        Assert.Empty(await Rules(db));
        Assert.Contains(result.Skipped, s => s.Reason == TurnFinalizer.JudgeUnavailableMessage);
    }

    // Mode Off disables automatic capture entirely: a no-op even with a Capture verdict.
    [Fact]
    public async Task ModeOff_IsNoOp()
    {
        await using var db = new TestDatabase(o => o.CaptureJudgeMode = "Off");
        await Init(db);

        var result = await Finalize(db, Turn(Capture(
            JudgeMemoryType.EngineeringLesson, JudgeCaptureReason.ExplicitUserSave)));

        Assert.True(result.IsEmpty);
        Assert.Empty(await Rules(db));
    }

    // AA. Manual `feedback add` still works through the existing explicit flow.
    [Fact]
    public async Task AA_ManualFeedbackAdd_StillWorks()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(
            ["feedback", "add", "--task", "work", "--feedback",
             "When writing SQL, use parameterized queries because injection is a risk.",
             "--scope-level", "Repository", "--scope-value", "project"],
            db.Services, output);

        Assert.Equal(0, code);
        Assert.NotEmpty(await Rules(db));
    }

    // AI. The judge decision metadata survives persistence to the status query.
    [Fact]
    public async Task AI_DecisionMetadata_PersistsAcrossReconstruct()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Finalize(db, Turn(Capture(JudgeMemoryType.EngineeringLesson, JudgeCaptureReason.ReviewerCorrection, 0.88)));

        await using var scope = db.CreateScope();
        var last = await scope.ServiceProvider.GetRequiredService<ITurnFinalizer>().GetLastAsync();

        Assert.Equal("SemanticCaptureJudge", last!.DecisionSource);
        Assert.Equal("Capture", last.Decision);
        Assert.Equal("ReviewerCorrection", last.JudgeReason);
        Assert.Equal(0.88, last.JudgeConfidence);
    }

    private static async Task<(int Code, string Output)> RunCli(TestDatabase db, JsonObject payload, params string[] args)
    {
        var originalIn = Console.In;
        Console.SetIn(new StringReader(payload.ToJsonString()));
        try
        {
            var output = new StringWriter();
            var code = await CommandRouter.RunAsync(args, db.Services, output);
            return (code, output.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }
}
