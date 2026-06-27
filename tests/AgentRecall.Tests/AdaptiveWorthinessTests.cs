using AgentRecall.Cli;
using AgentRecall.Cli.Hooks;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Finalization;
using AgentRecall.Core.Memory;
using AgentRecall.Core.Mining;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for outcome-aware (adaptive) worthiness: capture decisions that weigh not only
/// the candidate text but the evidence that produced it — an observed agent failure, a
/// user correction, an accepted review, a repeat. Split into pure policy tests (every
/// rule of <see cref="AdaptiveWorthinessPolicy"/>) and integration tests proving the
/// signals flow through <see cref="FeedbackService"/>, the turn finalizer, lesson mining,
/// activity notices, and <c>rules explain</c>.
/// </summary>
public class AdaptiveWorthinessTests
{
    // ---- Pure policy ----------------------------------------------------------

    private static AdaptiveWorthinessPolicy Policy(double autoBar = 0.5) =>
        new(new AgentRecallOptions { CaptureAutoConfidence = autoBar });

    private static MemoryWorthinessResult Worthy(double confidence = 0.5, RuleCategory category = RuleCategory.EngineeringLesson) =>
        new(MemoryWorthiness.WorthStoring, "Captures a reusable engineering lesson.", confidence, Category: category);

    private static MemoryWorthinessResult CodeFact(double confidence = 0.85) =>
        new(MemoryWorthiness.NotWorthStoring, "Looks like a method/property existence fact.", confidence, Category: RuleCategory.CodeFact);

    private static CaptureDecision Base(CaptureOutcome outcome = CaptureOutcome.AutoCapture, double confidence = 0.5) =>
        new(outcome, "Captures a reusable engineering lesson.", confidence, ScopeLevel.Repository, "skedda", "notice");

    // A. Generic best practice with no observed failure is skipped.
    [Fact]
    public void A_GenericBestPractice_NoFailure_Skips()
    {
        var result = Policy().Adjust(Worthy(0.5), new CaptureContext(), Base(), isDuplicate: false, conflictExists: false);

        Assert.Equal(CaptureOutcome.Skip, result.Outcome);
        Assert.Contains("no observed failure", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // B. Generic best practice with an observed agent failure is captured or suggested,
    //    and the reason names the observed failure.
    [Fact]
    public void B_GenericBestPractice_ObservedFailure_IsKept()
    {
        var context = new CaptureContext { ObservedFailure = true, UserCorrection = true };

        var result = Policy().Adjust(Worthy(0.5), context, Base(), isDuplicate: false, conflictExists: false);

        Assert.True(result.Outcome is CaptureOutcome.AutoCapture or CaptureOutcome.SuggestCapture);
        Assert.Equal(CaptureReason.ObservedAgentFailure, result.Reason);
    }

    // D. A bare code fact with an observed failure is never auto-captured.
    [Fact]
    public void D_CodeFact_ObservedFailure_NotAutoCaptured()
    {
        var context = new CaptureContext { ObservedFailure = true };

        var result = Policy().Adjust(CodeFact(), context, Base(CaptureOutcome.Skip, 0.85), isDuplicate: false, conflictExists: false);

        Assert.NotEqual(CaptureOutcome.AutoCapture, result.Outcome);
        Assert.Equal(CaptureOutcome.Skip, result.Outcome);
    }

    // D'. A code fact is parked for review only when explicitly saved — still not auto.
    [Fact]
    public void D_CodeFact_ExplicitSave_SuggestsNotAuto()
    {
        var context = new CaptureContext { ObservedFailure = true, ExplicitSaveRequest = true };

        var result = Policy().Adjust(CodeFact(), context, Base(CaptureOutcome.Skip, 0.85), isDuplicate: false, conflictExists: false);

        Assert.Equal(CaptureOutcome.SuggestCapture, result.Outcome);
    }

    // E. An accepted review comment on a worthy convention auto-captures.
    [Fact]
    public void E_AcceptedReview_AutoCaptures()
    {
        var context = new CaptureContext { ReviewAccepted = true };

        var result = Policy().Adjust(
            Worthy(0.55, RuleCategory.RepositoryConvention), context, Base(), isDuplicate: false, conflictExists: false);

        Assert.Equal(CaptureOutcome.AutoCapture, result.Outcome);
        Assert.Equal(CaptureReason.AcceptedReviewComment, result.Reason);
    }

    // F. A repeated correction raises confidence and strongly favours capture.
    [Fact]
    public void F_RepeatedCorrection_RaisesConfidence()
    {
        var once = new CaptureContext { UserCorrection = true };
        var repeated = new CaptureContext { UserCorrection = true, RepeatedCorrectionCount = 3 };

        var single = Policy().Adjust(Worthy(0.5), once, Base(), isDuplicate: false, conflictExists: false);
        var many = Policy().Adjust(Worthy(0.5), repeated, Base(), isDuplicate: false, conflictExists: false);

        Assert.True(many.Confidence > single.Confidence);
        Assert.Equal(CaptureReason.RepeatedCorrection, many.Reason);
        Assert.Equal(CaptureOutcome.AutoCapture, many.Outcome);
    }

    // G. A duplicate, even with observed failure, stores nothing new (the caller reinforces).
    [Fact]
    public void G_Duplicate_Skips()
    {
        var context = new CaptureContext { ObservedFailure = true };

        var result = Policy().Adjust(Worthy(0.9), context, Base(), isDuplicate: true, conflictExists: false);

        Assert.Equal(CaptureOutcome.Skip, result.Outcome);
        Assert.Contains("reinforced", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // H. A conflict holds the capture for review rather than auto-capturing.
    [Fact]
    public void H_Conflict_Suggests()
    {
        var context = new CaptureContext { ObservedFailure = true };

        var result = Policy().Adjust(Worthy(0.9), context, Base(), isDuplicate: false, conflictExists: true);

        Assert.Equal(CaptureOutcome.SuggestCapture, result.Outcome);
        Assert.Contains("conflict", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // I. An explicit do-not-save skips.
    [Fact]
    public void I_ExplicitDoNotSave_Skips()
    {
        var context = new CaptureContext { ObservedFailure = true, ExplicitDoNotSave = true };

        var result = Policy().Adjust(Worthy(0.9), context, Base(), isDuplicate: false, conflictExists: false);

        Assert.Equal(CaptureOutcome.Skip, result.Outcome);
        Assert.Contains("do-not-save", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // I'. An explicit save overrides a do-not-save.
    [Fact]
    public void I_ExplicitSave_OverridesDoNotSave()
    {
        var context = new CaptureContext { ExplicitDoNotSave = true, ExplicitSaveRequest = true };

        var result = Policy().Adjust(Worthy(0.2), context, Base(), isDuplicate: false, conflictExists: false);

        Assert.Equal(CaptureOutcome.AutoCapture, result.Outcome);
    }

    // J. An explicit save captures a worthy low-confidence lesson, even below the bar.
    [Fact]
    public void J_ExplicitSave_CapturesLowConfidenceLesson()
    {
        var context = new CaptureContext { ExplicitSaveRequest = true };

        var result = Policy(autoBar: 0.9).Adjust(Worthy(0.2), context, Base(confidence: 0.2), isDuplicate: false, conflictExists: false);

        Assert.Equal(CaptureOutcome.AutoCapture, result.Outcome);
    }

    // P. The adjustment is deterministic: same inputs, same output.
    [Fact]
    public void P_IsDeterministic()
    {
        var context = new CaptureContext { ObservedFailure = true, RepeatedCorrectionCount = 2 };

        var a = Policy().Adjust(Worthy(0.5), context, Base(), isDuplicate: false, conflictExists: false);
        var b = Policy().Adjust(Worthy(0.5), context, Base(), isDuplicate: false, conflictExists: false);

        Assert.Equal(a.Outcome, b.Outcome);
        Assert.Equal(a.Confidence, b.Confidence);
        Assert.Equal(a.Reason, b.Reason);
        Assert.Equal(a.Explanation, b.Explanation);
    }

    // ---- Integration: FeedbackService -----------------------------------------

    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    // C. The nested-if template regression is stored as a conditional, branch-preserving
    //    lesson with the observed-failure reason.
    [Fact]
    public async Task C_NestedIfRegression_StoredAsConditionalLesson()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var result = await service.AddAsync(new FeedbackInput
        {
            Task = "flatten validation template",
            Feedback = "The agent flattened nested {{#if}} blocks using (and ...) and changed {{else}} behavior.",
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "skedda",
            Context = new CaptureContext
            {
                Source = "turn-finalizer",
                ObservedFailure = true,
                UserCorrection = true,
                EvidenceSummary = "Agent changed {{else}} behavior while flattening nested conditionals; user corrected the implementation.",
            },
        });

        var rule = result.Rule!;
        Assert.Contains("flattening nested template conditionals", rule.Trigger, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("{{else}}", rule.RuleText, StringComparison.Ordinal);
        Assert.Contains("preserve", rule.RuleText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(and", rule.Mistake, StringComparison.Ordinal);
        Assert.Contains("else", rule.Mistake, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CaptureReason.ObservedAgentFailure, rule.CaptureReason);
    }

    // K. The capture reason and evidence are persisted on the rule.
    [Fact]
    public async Task K_CaptureReasonAndEvidence_ArePersisted()
    {
        await using var db = new TestDatabase();
        await Init(db);

        int ruleId;
        await using (var scope = db.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
            var result = await service.AddAsync(new FeedbackInput
            {
                Task = "emit validator messages",
                Feedback = "When emitting validator messages, apply the same tenant scope to avoid cross-tenant disclosure.",
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "skedda",
                Context = new CaptureContext
                {
                    Source = "turn-finalizer",
                    ObservedFailure = true,
                    EvidenceSummary = "Agent leaked another tenant's data; user corrected it.",
                },
            });
            ruleId = result.Rule!.Id;
            Assert.Equal(CaptureReason.ObservedAgentFailure, result.CaptureReason);
        }

        // Reload from the database to prove it round-tripped, not just the in-memory object.
        await using (var scope = db.CreateScope())
        {
            var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            var reloaded = await rules.GetAsync(ruleId);
            Assert.Equal(CaptureReason.ObservedAgentFailure, reloaded!.CaptureReason);
            Assert.Contains("leaked", reloaded.EvidenceSummary, StringComparison.OrdinalIgnoreCase);
        }
    }

    // The manual path (no context) is unchanged: no capture reason, normal capture.
    [Fact]
    public async Task ManualFeedback_NoContext_IsUnchanged()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var result = await service.AddAsync(new FeedbackInput
        {
            Task = "work",
            Feedback = "use parameterized queries",
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "skedda",
        });

        Assert.Equal(CaptureOutcome.AutoCapture, result.Decision!.Outcome);
        Assert.Equal(CaptureReason.None, result.Rule!.CaptureReason);
    }

    // ---- Integration: Turn Finalizer (M) --------------------------------------

    // M. The turn finalizer detects an observed-failure signal and passes it into
    //    adaptive worthiness, so the captured rule carries the observed-failure reason.
    [Fact]
    public async Task M_TurnFinalizer_PassesObservedFailureSignal()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var finalizer = scope.ServiceProvider.GetRequiredService<ITurnFinalizer>();

        var result = await finalizer.FinalizeAsync(new TurnFinalizationInput
        {
            Prompt = "That broke behavior. When emitting validator messages, apply the same tenant scope to avoid cross-tenant disclosure.",
            Source = "stop_hook",
            Cwd = "/repo/project",
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "project",
        });

        var lesson = Assert.Single(result.Captured);
        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var rule = await rules.GetAsync(lesson.RuleId);
        Assert.Equal(CaptureReason.ObservedAgentFailure, rule!.CaptureReason);
    }

    // A finalized turn with no outcome signal keeps the prior behaviour (generic →
    // suggested), proving the adaptive layer is additive and does not downgrade.
    [Fact]
    public async Task TurnFinalizer_NoOutcomeSignal_KeepsExistingBehaviour()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var finalizer = scope.ServiceProvider.GetRequiredService<ITurnFinalizer>();

        var result = await finalizer.FinalizeAsync(new TurnFinalizationInput
        {
            Prompt = "Don't re-query what you already loaded.",
            Source = "stop_hook",
            Cwd = "/repo/project",
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "project",
        });

        Assert.Empty(result.Captured);
        Assert.Single(result.Suggested);
    }

    // ---- Integration: Lesson Mining (N) ---------------------------------------

    private static async Task SeedSignal(TestDatabase db, string feedback)
    {
        await using var scope = db.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();
        await events.AddAsync(new RecallEvent
        {
            Type = RecallEventType.MistakeObserved,
            Trigger = "a task",
            Details = $"Feedback: {feedback}",
        });
    }

    // N. A mined candidate carries RepeatedCorrection once it recurs 3+ times, and the
    //    plain LessonMined reason at the minimum threshold.
    [Fact]
    public async Task N_MinedCandidate_CarriesCaptureReason()
    {
        await using var db = new TestDatabase();
        await Init(db);
        for (var i = 0; i < 3; i++) await SeedSignal(db, "Wrap external HTTP calls in a retry policy.");

        await using var scope = db.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ILessonMiningService>().MineAsync();

        var candidate = Assert.Single(result.Suggested);
        Assert.Equal(3, candidate.OccurrenceCount);
        Assert.Equal(CaptureReason.RepeatedCorrection, candidate.CaptureReason);
    }

    [Fact]
    public async Task N_MinedCandidate_AtThreshold_IsLessonMined()
    {
        await using var db = new TestDatabase();
        await Init(db);
        for (var i = 0; i < 2; i++) await SeedSignal(db, "Prefer composition over inheritance for handlers.");

        await using var scope = db.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ILessonMiningService>()
            .MineAsync(new MiningOptions { MinOccurrences = 2 });

        var candidate = Assert.Single(result.Suggested);
        Assert.Equal(2, candidate.OccurrenceCount);
        Assert.Equal(CaptureReason.LessonMined, candidate.CaptureReason);
    }

    // ---- Integration: Activity Notice (O) -------------------------------------

    // O. The capture notice names that the rule came from an observed mistake.
    [Fact]
    public void O_ActivityNotice_MentionsObservedMistake()
    {
        var rule = new RecallRule { Id = 24, RuleText = "Preserve else semantics when flattening nested conditionals." };
        var result = new FeedbackResult(null, rule)
        {
            Decision = new CaptureDecision(CaptureOutcome.AutoCapture, "reason", 0.9, ScopeLevel.Repository, "skedda", "notice"),
            CaptureReason = CaptureReason.ObservedAgentFailure,
        };

        var notice = ActivityNoticeFactory.ForFeedback(result, "cli")!;

        Assert.Equal(ActivityType.RuleCaptured, notice.Type);
        Assert.Contains("observed mistake", notice.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#24", notice.Details[0], StringComparison.Ordinal);
    }

    // A capture with no observed-mistake reason keeps the plain summary (no regression).
    [Fact]
    public void O_ActivityNotice_PlainSummary_WhenNoMistake()
    {
        var rule = new RecallRule { Id = 7, RuleText = "Use parameterized queries." };
        var result = new FeedbackResult(null, rule)
        {
            Decision = new CaptureDecision(CaptureOutcome.AutoCapture, "reason", 0.9, ScopeLevel.Repository, "skedda", "notice"),
            CaptureReason = CaptureReason.None,
        };

        var notice = ActivityNoticeFactory.ForFeedback(result, "cli")!;

        Assert.Equal("captured 1 new rule.", notice.Summary);
    }

    // O (live). The Stop-hook capture path detects the observed failure, captures with the
    //    observed-failure reason, and records the "observed mistake" notice.
    [Fact]
    public async Task O_CaptureHook_ObservedMistake_RecordsReasonAndNotice()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var payload = """
            {"prompt":"You changed semantics. When emitting validator messages, apply the same tenant scope to avoid cross-tenant disclosure.","cwd":"/repo/skedda"}
            """;
        var message = await CaptureHook.RunAsync(payload, db.Services, new StringWriter());
        Assert.NotNull(message);

        await using var scope = db.CreateScope();
        var rule = Assert.Single(await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync());
        Assert.Equal(CaptureReason.ObservedAgentFailure, rule.CaptureReason);

        var notice = await scope.ServiceProvider.GetRequiredService<IActivityRecorder>().GetLastAsync();
        Assert.NotNull(notice);
        Assert.Contains("observed mistake", notice!.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Integration: rules explain (L) ---------------------------------------

    // L. `rules explain` shows the capture reason and the evidence behind it.
    [Fact]
    public async Task L_RulesExplain_ShowsCaptureReasonAndEvidence()
    {
        await using var db = new TestDatabase();
        await Init(db);

        int ruleId;
        await using (var scope = db.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
            var result = await service.AddAsync(new FeedbackInput
            {
                Task = "flatten template",
                Feedback = "The agent flattened nested {{#if}} blocks using (and ...) and changed {{else}} behavior.",
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "skedda",
                Context = new CaptureContext
                {
                    Source = "turn-finalizer",
                    ObservedFailure = true,
                    UserCorrection = true,
                    EvidenceSummary = "Agent changed {{else}} behavior while flattening nested conditionals; user corrected the implementation.",
                },
            });
            ruleId = result.Rule!.Id;
        }

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["rules", "explain", ruleId.ToString()], db.Services, output);

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("Captured because:", text, StringComparison.Ordinal);
        Assert.Contains("ObservedAgentFailure", text, StringComparison.Ordinal);
        Assert.Contains("Evidence:", text, StringComparison.Ordinal);
        Assert.Contains("changed {{else}} behavior", text, StringComparison.Ordinal);
    }

    // ---- Integration: duplicate observed mistake reinforces (G) ---------------

    // G (integration). A repeated observed mistake reinforces the existing rule instead of
    //    creating a second one.
    [Fact]
    public async Task G_DuplicateObservedMistake_ReinforcesExisting()
    {
        await using var db = new TestDatabase();
        await Init(db);

        FeedbackInput Input() => new()
        {
            Task = "emit validator messages",
            Feedback = "When emitting validator messages, apply the same tenant scope to avoid cross-tenant disclosure.",
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "skedda",
            Context = new CaptureContext { Source = "turn-finalizer", ObservedFailure = true },
        };

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var first = await service.AddAsync(Input());
        var second = await service.AddAsync(Input());

        Assert.False(first.ReusedExistingRule);
        Assert.True(second.ReusedExistingRule);
        Assert.Equal(first.Rule!.Id, second.Rule!.Id);

        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        Assert.Single(await rules.ListAsync());
    }

    // Q. The harness is isolated to a temp directory, never the real home store.
    [Fact]
    public async Task Q_TestDatabase_IsIsolated()
    {
        await using var db = new TestDatabase();

        Assert.StartsWith(Path.GetTempPath(), db.Options.DataDirectory);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.DoesNotContain(Path.Combine(home, ".agentrecall"), db.Options.DatabasePath);
    }
}
