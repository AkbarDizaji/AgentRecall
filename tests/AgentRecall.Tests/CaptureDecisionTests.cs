using System.Text.Json.Nodes;
using AgentRecall.Cli.Hooks;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for the deterministic capture decision — the final step that decides, inside
/// AgentRecall, whether to AutoCapture, SuggestCapture, or Skip a candidate so the user
/// is almost never asked. Split into pure policy tests (every branch of
/// <see cref="CaptureDecisionPolicy"/>) and integration tests that prove each outcome
/// reaches the right place through <see cref="FeedbackService"/> and the capture hook.
/// </summary>
public class CaptureDecisionTests
{
    // ---- Pure policy: every branch is reachable and deterministic --------------

    private static CaptureDecisionPolicy Policy(double autoBar = 0.5) =>
        new(new AgentRecallOptions { CaptureAutoConfidence = autoBar });

    private static CaptureSignals Signals(
        bool worthy = true,
        double confidence = 0.9,
        bool explicitAcceptance = false,
        bool approvePosture = true,
        bool isDuplicate = false,
        bool codeFactOverrideAllowed = false,
        bool isExplicitUserPreference = false,
        ScopeLevel scopeLevel = ScopeLevel.Repository,
        string? scopeValue = "skedda",
        string worthinessReason = "Captures a reusable engineering lesson.") =>
        new()
        {
            Worthy = worthy,
            Confidence = confidence,
            ExplicitAcceptance = explicitAcceptance,
            ApprovePosture = approvePosture,
            IsDuplicate = isDuplicate,
            CodeFactOverrideAllowed = codeFactOverrideAllowed,
            IsExplicitUserPreference = isExplicitUserPreference,
            ScopeLevel = scopeLevel,
            ScopeValue = scopeValue,
            WorthinessReason = worthinessReason,
        };

    [Fact]
    public void Decide_NullSignals_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Policy().Decide(null!));
    }

    [Fact]
    public void Decide_Duplicate_Skips()
    {
        var decision = Policy().Decide(Signals(isDuplicate: true));

        Assert.Equal(CaptureOutcome.Skip, decision.Outcome);
        Assert.Contains("already exists", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reinforced", decision.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_NotWorthy_Skips()
    {
        var decision = Policy().Decide(Signals(
            worthy: false,
            confidence: 0.85,
            worthinessReason: "Looks like a method/property existence fact."));

        Assert.Equal(CaptureOutcome.Skip, decision.Outcome);
        Assert.Contains("existence fact", decision.Reason, StringComparison.OrdinalIgnoreCase);
        // A plain code-fact skip carries no notice — only the accepted-override path does.
        Assert.Equal(string.Empty, decision.Notice);
    }

    [Fact]
    public void Decide_NotWorthy_AcceptedOverride_AutoCaptures()
    {
        // An accepted code fact only stores when the override is enabled.
        var decision = Policy().Decide(Signals(
            worthy: false,
            explicitAcceptance: true,
            approvePosture: true,
            codeFactOverrideAllowed: true));

        Assert.Equal(CaptureOutcome.AutoCapture, decision.Outcome);
        Assert.Contains("overrides the code-fact filter", decision.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_NotWorthy_AcceptedButOverrideDisallowed_StillSkips()
    {
        // Explicit acceptance alone is not enough: the override flag must also be set.
        var decision = Policy().Decide(Signals(
            worthy: false,
            explicitAcceptance: true,
            approvePosture: true,
            codeFactOverrideAllowed: false));

        Assert.Equal(CaptureOutcome.Skip, decision.Outcome);
    }

    [Fact]
    public void Decide_NotWorthy_OverrideAllowedButPostureOff_StillSkips()
    {
        // The override needs both approve posture on AND the flag — either alone skips.
        var decision = Policy().Decide(Signals(
            worthy: false,
            approvePosture: false,
            codeFactOverrideAllowed: true));

        Assert.Equal(CaptureOutcome.Skip, decision.Outcome);
    }

    [Fact]
    public void Decide_Worthy_ExplicitAcceptance_AutoCaptures_EvenAtLowConfidence()
    {
        // Acceptance is the strongest signal: it wins regardless of confidence/posture.
        var decision = Policy().Decide(Signals(
            confidence: 0.1,
            explicitAcceptance: true,
            approvePosture: false));

        Assert.Equal(CaptureOutcome.AutoCapture, decision.Outcome);
        Assert.Contains("acceptance signal was strong", decision.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_Worthy_ExplicitUserPreference_AutoCaptures_EvenWithPostureOff()
    {
        // A stated preference is captured on the user's word, independent of posture.
        var decision = Policy().Decide(Signals(
            confidence: 0.1,
            approvePosture: false,
            isExplicitUserPreference: true));

        Assert.Equal(CaptureOutcome.AutoCapture, decision.Outcome);
        Assert.Contains("explicitly stated user preference", decision.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_Worthy_PostureOff_Suggests()
    {
        var decision = Policy().Decide(Signals(confidence: 0.9, approvePosture: false));

        Assert.Equal(CaptureOutcome.SuggestCapture, decision.Outcome);
        Assert.Contains("auto-approve is off", decision.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_Worthy_PostureOn_ConfidenceMeetsBar_AutoCaptures()
    {
        var decision = Policy(autoBar: 0.6).Decide(Signals(confidence: 0.7, approvePosture: true));

        Assert.Equal(CaptureOutcome.AutoCapture, decision.Outcome);
        Assert.Contains("met the auto-capture bar", decision.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_Worthy_PostureOn_ConfidenceBelowBar_Suggests()
    {
        // The genuinely-ambiguous case the user described: worthy, but confidence too
        // low to act on alone, so AgentRecall asks instead of guessing.
        var decision = Policy(autoBar: 0.95).Decide(Signals(confidence: 0.9, approvePosture: true));

        Assert.Equal(CaptureOutcome.SuggestCapture, decision.Outcome);
        Assert.Contains("below the auto-capture bar", decision.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_IsDeterministic()
    {
        var signals = Signals(confidence: 0.72, approvePosture: true);
        var a = Policy(autoBar: 0.7).Decide(signals);
        var b = Policy(autoBar: 0.7).Decide(signals);

        Assert.Equal(a.Outcome, b.Outcome);
        Assert.Equal(a.Reason, b.Reason);
        Assert.Equal(a.Notice, b.Notice);
        Assert.Equal(a.Confidence, b.Confidence);
    }

    [Fact]
    public void ScopeLabel_FormatsRepositoryAndGlobal()
    {
        Assert.Equal("Repository:skedda", Policy().Decide(Signals()).ScopeLabel);
        Assert.Equal("Global", Policy().Decide(Signals(scopeLevel: ScopeLevel.Global, scopeValue: null)).ScopeLabel);
    }

    // A non-Global scope with no value falls back to the bare scope-level name, not
    // "Repository:" with a missing tail — the null/blank branch is distinct from Global.
    [Fact]
    public void ScopeLabel_NonGlobalWithNoValue_FallsBackToScopeLevelName()
    {
        Assert.Equal("Repository", Policy().Decide(Signals(scopeLevel: ScopeLevel.Repository, scopeValue: null)).ScopeLabel);
        Assert.Equal("Repository", Policy().Decide(Signals(scopeLevel: ScopeLevel.Repository, scopeValue: "   ")).ScopeLabel);
    }

    // Global short-circuits to the plain "Global" label even when a scope value happens to be
    // set — it must not fall through to the "{ScopeLevel}:{ScopeValue}" branch.
    [Fact]
    public void ScopeLabel_GlobalWithNonNullScopeValue_StillShowsPlainGlobal()
    {
        Assert.Equal("Global", Policy().Decide(Signals(scopeLevel: ScopeLevel.Global, scopeValue: "skedda")).ScopeLabel);
    }

    [Fact]
    public void CaptureSignals_WorthinessReason_DefaultsToEmpty()
    {
        var signals = new CaptureSignals { Worthy = true, Confidence = 0.5 };
        Assert.Equal(string.Empty, signals.WorthinessReason);
    }

    // ---- Integration through FeedbackService: each outcome lands correctly -----

    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static FeedbackInput Lesson(string feedback, bool? autoApprove = null) => new()
    {
        Task = "some work",
        Feedback = feedback,
        ScopeLevel = ScopeLevel.Repository,
        ScopeValue = "skedda",
        AutoApprove = autoApprove,
    };

    [Fact]
    public async Task AddFeedback_DefaultPosture_AutoCaptures_ActiveRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var result = await service.AddAsync(Lesson("use parameterized queries"));

        Assert.Equal(CaptureOutcome.AutoCapture, result.Decision!.Outcome);
        Assert.NotNull(result.Rule);
        Assert.Equal(RuleStatus.Active, result.Rule!.Status);
    }

    [Fact]
    public async Task AddFeedback_PostureOff_Suggests_PendingRule()
    {
        await using var db = new TestDatabase(o => o.AutoApproveFeedback = false);
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var result = await service.AddAsync(Lesson("use parameterized queries"));

        Assert.Equal(CaptureOutcome.SuggestCapture, result.Decision!.Outcome);
        Assert.NotNull(result.Rule);
        Assert.Equal(RuleStatus.Pending, result.Rule!.Status);
    }

    [Fact]
    public async Task AddFeedback_AmbiguousConfidence_Suggests_EvenWithPostureOn()
    {
        // Raise the auto-capture bar above the candidate's confidence so the
        // confidence-driven suggest band fires in the normal (posture-on) flow.
        await using var db = new TestDatabase(o => o.CaptureAutoConfidence = 0.95);
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        // A clear engineering lesson scores 0.90 — still below the 0.95 bar.
        var result = await service.AddAsync(
            Lesson("When implementing feature gates, ensure frontend and backend use the same definition."));

        Assert.Equal(CaptureOutcome.SuggestCapture, result.Decision!.Outcome);
        Assert.Equal(RuleStatus.Pending, result.Rule!.Status);
    }

    [Fact]
    public async Task AddFeedback_ExplicitAcceptance_AutoCaptures_OverPostureOff()
    {
        // Posture off would normally suggest; explicit acceptance auto-captures anyway.
        await using var db = new TestDatabase(o => o.AutoApproveFeedback = false);
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var result = await service.AddAsync(Lesson("use parameterized queries", autoApprove: true));

        Assert.Equal(CaptureOutcome.AutoCapture, result.Decision!.Outcome);
        Assert.Equal(RuleStatus.Active, result.Rule!.Status);
    }

    [Fact]
    public async Task AddFeedback_CodeFact_Skips_NoRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var result = await service.AddAsync(Lesson("Use IsEventsFeatureEnabled."));

        Assert.Equal(CaptureOutcome.Skip, result.Decision!.Outcome);
        Assert.Null(result.Rule);
        Assert.False(result.ReusedExistingRule);
    }

    [Fact]
    public async Task AddFeedback_Duplicate_Skips_ReinforcesExisting()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var first = await service.AddAsync(Lesson("use parameterized queries"));
        var second = await service.AddAsync(Lesson("use parameterized queries"));

        Assert.Equal(CaptureOutcome.AutoCapture, first.Decision!.Outcome);
        Assert.Equal(CaptureOutcome.Skip, second.Decision!.Outcome);
        Assert.True(second.ReusedExistingRule);
        Assert.Equal(first.Rule!.Id, second.Rule!.Id);

        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        Assert.Single(await rules.ListAsync());
    }

    // ---- Capture hook surfaces the decision as a notification ------------------

    private static string Payload(string prompt, bool? accepted = null)
    {
        var node = new JsonObject
        {
            ["prompt"] = prompt,
            ["cwd"] = "/tmp/project",
            ["hook_event_name"] = "Stop",
        };
        if (accepted is { } a)
        {
            node["accepted"] = a;
        }

        return node.ToJsonString();
    }

    [Fact]
    public async Task Hook_AutoCapture_NotifiesWithReasonAndConfidence()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var message = await CaptureHook.RunAsync(
            Payload("We do not mock DbContext directly."), db.Services, new StringWriter());

        Assert.NotNull(message);
        Assert.Contains("AgentRecall captured rule", message!, StringComparison.Ordinal);
        Assert.Contains("Reason:", message, StringComparison.Ordinal);
        Assert.Contains("Notice:", message, StringComparison.Ordinal);
        Assert.Contains("confidence", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hook_SuggestCapture_NotifiesAndNamesTheOneAction()
    {
        // Posture off → a worthy correction is suggested, not auto-captured.
        await using var db = new TestDatabase(o => o.AutoApproveFeedback = false);
        await Init(db);

        var message = await CaptureHook.RunAsync(
            Payload("We do not mock DbContext directly."), db.Services, new StringWriter());

        Assert.NotNull(message);
        Assert.Contains("pending suggestion", message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rules approve", message, StringComparison.Ordinal);

        // It really is parked as Pending, not activated.
        await using var scope = db.CreateScope();
        var rule = Assert.Single(await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync());
        Assert.Equal(RuleStatus.Pending, rule.Status);
    }
}
