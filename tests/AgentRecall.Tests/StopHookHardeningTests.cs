using System.Text.Json.Nodes;
using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for Stop-hook capture hardening: the deterministic quality gate that keeps
/// assistant prose, meta commentary, malformed conversation fragments, and any
/// do-not-save turn out of memory, plus the structured skip reasons surfaced by
/// capture-status / turn-summary / activity, and the `cleanup pending-noise` command.
/// Everything is offline and deterministic; each test uses a throwaway database.
/// </summary>
[Collection("ConsoleStdin")]
public class StopHookHardeningTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static TurnFinalizationInput Turn(string? prompt = null, string? assistant = null, bool? accepted = null) =>
        new()
        {
            Prompt = prompt,
            AssistantResponse = assistant,
            Accepted = accepted,
            Source = "stop_hook",
            Cwd = "/repo/project",
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "project",
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

    private static void AssertNothingStored(TurnFinalizationResult result)
    {
        Assert.Empty(result.Captured);
        Assert.Empty(result.Suggested);
    }

    // ---- Gate unit checks (deterministic, no DB) ------------------------------

    // D. Assistant prose "One thing is worth saving..." without a real rule is skipped.
    [Fact]
    public void Gate_OneThingWorthSavingProse_IsAssistantProse()
    {
        var result = StopHookCandidateGate.ScreenText(
            "One thing is worth saving — a workflow gotcha not in any doc.");
        Assert.False(result.IsAcceptable);
        Assert.Equal(CaptureSkipReason.AssistantProse, result.Reason);
    }

    // E. "Want me to save it?" is skipped.
    [Fact]
    public void Gate_WantMeToSaveIt_IsAssistantProse()
    {
        Assert.Equal(CaptureSkipReason.AssistantProse,
            StopHookCandidateGate.ScreenText("Want me to save it?").Reason);
    }

    // F. "I didn't manually call AgentRecall" is skipped.
    [Fact]
    public void Gate_IDidntManuallyCall_IsAssistantProse()
    {
        Assert.Equal(CaptureSkipReason.AssistantProse,
            StopHookCandidateGate.ScreenText("I didn't manually call AgentRecall, the hook fires on its own.").Reason);
    }

    // G. "The Stop hook may have captured it" is skipped.
    [Fact]
    public void Gate_StopHookMayHaveCaptured_IsAssistantProse()
    {
        Assert.Equal(CaptureSkipReason.AssistantProse,
            StopHookCandidateGate.ScreenText("The Stop hook may have captured it.").Reason);
    }

    // H. Malformed trigger "When working on Not much..." is rejected.
    [Fact]
    public void Gate_MalformedConversationTrigger_IsRejected()
    {
        Assert.True(StopHookCandidateGate.IsMalformedTrigger(
            "When working on Not much. Most of this chat lives here."));

        var assessed = StopHookCandidateGate.Assess(
            "Keep JSON on stdout and status on stderr.",
            "When working on Not much. Most of this chat lives here.");
        Assert.Equal(CaptureSkipReason.MalformedTrigger, assessed.Reason);
    }

    // I. Candidate missing action is rejected.
    [Fact]
    public void Gate_ConditionWithNoAction_IsMissingAction()
    {
        Assert.Equal(CaptureSkipReason.MissingAction,
            StopHookCandidateGate.ScreenText("When reporting the state to the user.").Reason);
    }

    // J. Candidate missing condition/trigger is rejected.
    [Fact]
    public void Gate_MissingTrigger_IsMalformed()
    {
        Assert.Equal(CaptureSkipReason.MalformedTrigger,
            StopHookCandidateGate.Assess("Keep resources tidy and dispose them.", triggerText: null).Reason);
    }

    // K. Candidate with condition + action but no reason is not hard-rejected (policy downgrades).
    [Fact]
    public void Gate_ConditionActionNoReason_IsAccepted()
    {
        Assert.True(StopHookCandidateGate.ScreenText("When writing SQL, use parameterized queries.").IsAcceptable);
    }

    // A clean AgentRecall behaviour convention passes the gate (contrast with prose about it).
    [Fact]
    public void Gate_CleanAgentRecallConvention_IsAccepted()
    {
        Assert.True(StopHookCandidateGate.ScreenText(
            "When reporting AgentRecall memory state, check capture-status or turn-summary and answer from actual state instead of guessing.").IsAcceptable);
    }

    // ---- Explicit do-not-save (finalizer) -------------------------------------

    // A. English do-not-save prevents capture and Pending creation.
    [Fact]
    public async Task A_EnglishDoNotSave_CapturesNothing()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            prompt: "Use parameterized queries to avoid SQL injection. Don't save this."));

        AssertNothingStored(result);
        Assert.Contains(result.Skipped, s => s.SkipReason == CaptureSkipReason.ExplicitDoNotSave);
        Assert.Empty(await Rules(db));
    }

    // B. Persian do-not-save prevents capture.
    [Fact]
    public async Task B_PersianDoNotSave_CapturesNothing()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            prompt: "همیشه از پارامتر استفاده کن تا از تزریق جلوگیری بشه. این رو ذخیره نکن."));

        AssertNothingStored(result);
        Assert.Contains(result.Skipped, s => s.SkipReason == CaptureSkipReason.ExplicitDoNotSave);
        Assert.Empty(await Rules(db));
    }

    // C. Finglish do-not-save prevents capture.
    [Theory]
    [InlineData("Use parameterized queries because injection. save nakon.")]
    [InlineData("Use parameterized queries because injection. capture nakon.")]
    [InlineData("Use parameterized queries because injection. too AgentRecall nazar.")]
    public async Task C_FinglishDoNotSave_CapturesNothing(string prompt)
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(prompt: prompt));

        AssertNothingStored(result);
        Assert.Empty(await Rules(db));
    }

    // AH. do-not-save + save: the most recent intent wins; ties prefer do-not-save.
    [Fact]
    public async Task AH_MostRecentIntentWins_AndTiesPreferDoNotSave()
    {
        // Save last → capture allowed.
        await using (var db = new TestDatabase())
        {
            await Init(db);
            var result = await Finalize(db, Turn(
                prompt: "Don't save this. Actually, save this: when writing SQL, use parameterized queries because injection."));
            Assert.NotEmpty(result.Captured.Concat(result.Suggested));
        }

        // Do-not-save last → nothing captured.
        await using (var db = new TestDatabase())
        {
            await Init(db);
            var result = await Finalize(db, Turn(
                prompt: "Save this: when writing SQL use parameterized queries because injection. Actually don't save this."));
            AssertNothingStored(result);
        }
    }

    // ---- Assistant prose / vague candidates (finalizer) -----------------------

    // The reported bug: assistant prose becomes no rule (captured or Pending).
    [Fact]
    public async Task ReportedBug_AssistantProse_ProducesNoRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            assistant: "Not much. Most of this chat lives here. " +
                       "One thing is worth saving — a workflow gotcha not in any doc."));

        AssertNothingStored(result);
        Assert.Contains(result.Skipped, s => s.SkipReason == CaptureSkipReason.AssistantProse);
        Assert.Empty(await Rules(db));
    }

    // O. The quality gate prevents a vague Pending rule.
    [Fact]
    public async Task O_VagueSelfIdentified_IsNotSuggestedAsPending()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            assistant: "This is worth storing: this is important."));

        Assert.Empty(result.Suggested);
        Assert.Empty(result.Captured);
    }

    // ---- Explicit save still works, but never stores garbage ------------------

    // L. Clean explicit save request still captures with condition/action/reason.
    [Fact]
    public async Task L_CleanExplicitSave_Captures()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            prompt: "Save this: When writing Claude Code hooks, keep JSON on stdout and status/debug " +
                    "messages on stderr because stdout is consumed by protocol readers."));

        Assert.NotEmpty(result.Captured.Concat(result.Suggested));
        var stored = (await Rules(db)).Single();
        Assert.Contains("stdout", stored.RuleText, StringComparison.OrdinalIgnoreCase);
    }

    // M. Explicit save with garbage text does not store raw prose.
    [Fact]
    public async Task M_ExplicitSaveWithGarbage_StoresNoRawProse()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            prompt: "Save this: one thing is worth saving, not much really."));

        AssertNothingStored(result);
        Assert.DoesNotContain(await Rules(db), r =>
            r.RuleText.Contains("not much", StringComparison.OrdinalIgnoreCase));
    }

    // N. A clean AgentRecall behaviour convention can still be captured.
    [Fact]
    public async Task N_CleanAgentRecallConvention_IsCaptured()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            prompt: "Save this: When reporting AgentRecall memory state, check capture-status or " +
                    "turn-summary and answer from actual state instead of guessing."));

        Assert.NotEmpty(result.Captured.Concat(result.Suggested));
    }

    // Z. Existing good capture behaviour does not regress.
    [Fact]
    public async Task Z_StrongSelfIdentifiedLesson_StillCaptures()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            assistant: "One worth storing is: when validators load entities before controller execution, " +
                       "apply the same tenant scope before emitting entity-specific messages."));

        Assert.Single(result.Captured);
    }

    // ---- capture-status / turn-summary / activity -----------------------------

    // R. capture-status shows the skip reason clearly.
    [Fact]
    public async Task R_CaptureStatus_ShowsSkipReason()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Finalize(db, Turn(prompt: "Use parameterized queries. Don't save this."));

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["capture-status", "--last-turn"], db.Services, output);

        Assert.Equal(0, code);
        Assert.Contains("do-not-save", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // Q. Turn Summary shows the skip reason compactly.
    [Fact]
    public async Task Q_TurnSummary_ShowsSkipReason()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Finalize(db, Turn(
            assistant: "One thing is worth saving — a workflow gotcha not in any doc."));

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["turn-summary", "--last", "--detailed"], db.Services, output);

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("Skipped", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("assistant prose", text, StringComparison.OrdinalIgnoreCase);
    }

    // P + AF. A skipped candidate is recorded as structured activity with a capped excerpt.
    [Fact]
    public async Task P_SkippedCandidate_IsRecordedAsActivity_WithCappedExcerpt()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var originalIn = Console.In;
        Console.SetIn(new StringReader(new JsonObject
        {
            ["assistant_response"] = "One thing is worth saving — a workflow gotcha not in any doc.",
            ["cwd"] = "/repo/project",
            ["source"] = "stop_hook",
        }.ToJsonString()));
        try
        {
            var sink = new StringWriter();
            await CommandRouter.RunAsync(["finalize-turn", "--hook"], db.Services, sink);
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        await using var scope = db.CreateScope();
        var activities = await scope.ServiceProvider
            .GetRequiredService<IAgentRecallActivityRepository>().ListAsync();
        var skip = Assert.Single(activities, a => a.ActivityType == ActivityType.CandidateSkipped);
        Assert.Contains("assistant prose", skip.Details ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        // Excerpt is capped — never a full transcript.
        Assert.True((skip.Details ?? string.Empty).Length < 400);
    }

    // S. The Stop hook remains non-blocking (exit 0) even on a skipped turn.
    [Fact]
    public async Task S_StopHook_RemainsNonBlocking()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var originalIn = Console.In;
        Console.SetIn(new StringReader(new JsonObject
        {
            ["prompt"] = "Don't save this.",
            ["cwd"] = "/repo/project",
        }.ToJsonString()));
        try
        {
            var output = new StringWriter();
            var code = await CommandRouter.RunAsync(["finalize-turn", "--hook"], db.Services, output);
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    // ---- cleanup pending-noise ------------------------------------------------

    private static async Task<RecallRule> AddRuleAsync(
        TestDatabase db, string ruleText, string trigger, RuleStatus status, string tags = "turn-finalizer", int version = 1)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().AddAsync(new RecallRule
        {
            RuleText = ruleText,
            Trigger = trigger,
            Status = status,
            Tags = tags,
            Version = version,
            Category = RuleCategory.RepositoryConvention,
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "project",
            Confidence = 0.4,
        });
    }

    private static async Task SeedNoiseAsync(TestDatabase db)
    {
        await AddRuleAsync(db, "One thing is worth saving — a workflow gotcha not in any doc.", "When working on Not much", RuleStatus.Pending);
        await AddRuleAsync(db, "Want me to save it?", "When working on this chat", RuleStatus.Pending);
        await AddRuleAsync(db, "Keep JSON on stdout and status on stderr.", "When working on Not much. Most of this chat lives here", RuleStatus.Pending);
        // A clean Pending rule that must be preserved.
        await AddRuleAsync(db, "When writing SQL, use parameterized queries because injection is a risk.", "When writing SQL", RuleStatus.Pending);
        // An Active rule (never touched by cleanup).
        await AddRuleAsync(db, "Blah blah not much really here.", "When working on chat", RuleStatus.Active);
        // A user-modified (versioned) Pending rule (never touched).
        await AddRuleAsync(db, "One thing is worth saving here.", "When working on chat", RuleStatus.Pending, version: 2);
    }

    private static async Task<(int Code, string Output)> RunAsync(TestDatabase db, params string[] args)
    {
        var writer = new StringWriter();
        var code = await CommandRouter.RunAsync(args, db.Services, writer);
        return (code, writer.ToString());
    }

    // T. Cleanup dry-run finds noisy Pending turn-finalizer rules and archives nothing.
    [Fact]
    public async Task T_CleanupDryRun_FindsNoiseArchivesNothing()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedNoiseAsync(db);

        var (code, output) = await RunAsync(db, "cleanup", "pending-noise");

        Assert.Equal(0, code);
        Assert.Contains("found", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--apply", output, StringComparison.Ordinal);
        // Nothing archived on a dry run.
        Assert.DoesNotContain(await Rules(db), r => r.Status == RuleStatus.Archived);
    }

    // U. Cleanup --apply archives noisy Pending turn-finalizer rules.
    [Fact]
    public async Task U_CleanupApply_ArchivesNoise()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedNoiseAsync(db);

        var (code, output) = await RunAsync(db, "cleanup", "pending-noise", "--apply");

        Assert.Equal(0, code);
        Assert.Contains("archived", output, StringComparison.OrdinalIgnoreCase);
        var archived = (await Rules(db)).Where(r => r.Status == RuleStatus.Archived).ToList();
        Assert.NotEmpty(archived);
        Assert.All(archived, r => Assert.Contains("turn-finalizer", r.Tags, StringComparison.Ordinal));
    }

    // V. Cleanup does not archive Active rules.
    // W. Cleanup does not archive clean Pending rules.
    // X. Cleanup does not archive user-modified rules.
    [Fact]
    public async Task VWX_Cleanup_PreservesActiveCleanAndUserModified()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedNoiseAsync(db);

        await RunAsync(db, "cleanup", "pending-noise", "--apply");

        var rules = await Rules(db);
        // V: the Active rule survives.
        Assert.Contains(rules, r => r.Status == RuleStatus.Active);
        // W: the clean Pending rule survives.
        Assert.Contains(rules, r =>
            r.Status == RuleStatus.Pending && r.RuleText.Contains("parameterized queries", StringComparison.OrdinalIgnoreCase));
        // X: the user-modified (versioned) rule survives.
        Assert.Contains(rules, r => r.Version > 1 && r.Status != RuleStatus.Archived);
    }

    // Y. Cleanup JSON is valid and deterministic.
    [Fact]
    public async Task Y_CleanupJson_IsValidAndShaped()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedNoiseAsync(db);

        var (code, output) = await RunAsync(db, "cleanup", "pending-noise", "--json");

        Assert.Equal(0, code);
        var node = JsonNode.Parse(output)!;
        Assert.True(node["matched"]!.GetValue<int>() >= 1);
        Assert.Equal(0, node["archived"]!.GetValue<int>());
        Assert.True(node["dryRun"]!.GetValue<bool>());
        Assert.NotNull(node["reasons"]);
    }

    // AG. Duplicate noise is not repeatedly created across turns.
    [Fact]
    public async Task AG_DuplicateNoise_IsNotRecreatedAcrossTurns()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var turn = Turn(assistant: "One thing is worth saving — a workflow gotcha not in any doc.");
        await Finalize(db, turn);
        await Finalize(db, turn);

        // The prose never became a rule on either turn.
        Assert.Empty(await Rules(db));
    }
}
