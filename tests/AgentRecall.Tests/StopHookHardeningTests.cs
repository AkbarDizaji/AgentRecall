using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for the deterministic Stop-hook quality gate (<see cref="StopHookCandidateGate"/>) and
/// the <c>cleanup pending-noise</c> command. The gate no longer decides live capture — the
/// semantic capture judge does (see <see cref="CaptureJudgeFinalizerTests"/>) — but it is still
/// the shared screen behind <c>cleanup pending-noise</c>, which finds and archives noisy rules
/// created before the judge existed. Everything here is offline and deterministic.
/// </summary>
[Collection("ConsoleStdin")]
public class StopHookHardeningTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task<IReadOnlyList<RecallRule>> Rules(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync();
    }

    // ---- Gate unit checks (deterministic, no DB) ------------------------------

    [Fact]
    public void Gate_OneThingWorthSavingProse_IsAssistantProse()
    {
        var result = StopHookCandidateGate.ScreenText(
            "One thing is worth saving — a workflow gotcha not in any doc.");
        Assert.False(result.IsAcceptable);
        Assert.Equal(CaptureSkipReason.AssistantProse, result.Reason);
    }

    [Fact]
    public void Gate_WantMeToSaveIt_IsAssistantProse()
    {
        Assert.Equal(CaptureSkipReason.AssistantProse,
            StopHookCandidateGate.ScreenText("Want me to save it?").Reason);
    }

    [Fact]
    public void Gate_IDidntManuallyCall_IsAssistantProse()
    {
        Assert.Equal(CaptureSkipReason.AssistantProse,
            StopHookCandidateGate.ScreenText("I didn't manually call AgentRecall, the hook fires on its own.").Reason);
    }

    [Fact]
    public void Gate_StopHookMayHaveCaptured_IsAssistantProse()
    {
        Assert.Equal(CaptureSkipReason.AssistantProse,
            StopHookCandidateGate.ScreenText("The Stop hook may have captured it.").Reason);
    }

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

    [Fact]
    public void Gate_ConditionWithNoAction_IsMissingAction()
    {
        Assert.Equal(CaptureSkipReason.MissingAction,
            StopHookCandidateGate.ScreenText("When reporting the state to the user.").Reason);
    }

    [Fact]
    public void Gate_MissingTrigger_IsMalformed()
    {
        Assert.Equal(CaptureSkipReason.MalformedTrigger,
            StopHookCandidateGate.Assess("Keep resources tidy and dispose them.", triggerText: null).Reason);
    }

    [Fact]
    public void Gate_ConditionActionNoReason_IsAccepted()
    {
        Assert.True(StopHookCandidateGate.ScreenText("When writing SQL, use parameterized queries.").IsAcceptable);
    }

    [Fact]
    public void Gate_CleanAgentRecallConvention_IsAccepted()
    {
        Assert.True(StopHookCandidateGate.ScreenText(
            "When reporting AgentRecall memory state, check capture-status or turn-summary and answer from actual state instead of guessing.").IsAcceptable);
    }

    // An off-topic aside is rejected even when it contains a keyword ("validation") that would
    // otherwise read as a security concern — the "Off topic:" opener marks it a digression.
    [Fact]
    public void Gate_OffTopicAsideWithKeyword_IsOffTopic()
    {
        Assert.Equal(CaptureSkipReason.OffTopic,
            StopHookCandidateGate.ScreenText(
                "Off topic: in the registration modal we change the button according to validation " +
                "and say the event is full and you can join the waitlist.").Reason);
    }

    [Theory]
    [InlineData("Unrelated, but the dashboard loads slowly on Safari.")]
    [InlineData("Side note: the marketing site uses a different colour palette.")]
    [InlineData("By the way, the staging URL changed last week.")]
    public void Gate_TangentOpeners_AreOffTopic(string text)
    {
        Assert.Equal(CaptureSkipReason.OffTopic, StopHookCandidateGate.ScreenText(text).Reason);
    }

    [Fact]
    public void Gate_TangentWordMidRule_IsAccepted()
    {
        Assert.True(StopHookCandidateGate.ScreenText(
            "When validating input, run every check on the same code path to avoid going off on a tangent.")
            .IsAcceptable);
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

    // V/W/X. Cleanup preserves Active, clean Pending, and user-modified rules.
    [Fact]
    public async Task VWX_Cleanup_PreservesActiveCleanAndUserModified()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedNoiseAsync(db);

        await RunAsync(db, "cleanup", "pending-noise", "--apply");

        var rules = await Rules(db);
        Assert.Contains(rules, r => r.Status == RuleStatus.Active);
        Assert.Contains(rules, r =>
            r.Status == RuleStatus.Pending && r.RuleText.Contains("parameterized queries", StringComparison.OrdinalIgnoreCase));
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
        var node = System.Text.Json.Nodes.JsonNode.Parse(output)!;
        Assert.True(node["matched"]!.GetValue<int>() >= 1);
        Assert.Equal(0, node["archived"]!.GetValue<int>());
        Assert.True(node["dryRun"]!.GetValue<bool>());
        Assert.NotNull(node["reasons"]);
    }
}
