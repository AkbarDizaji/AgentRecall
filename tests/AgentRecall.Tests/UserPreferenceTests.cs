using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Preferences;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for explicit user-preference capture: an explicitly stated communication or
/// interaction preference is recognised (English and Persian), normalized into durable
/// bounded guidance, and captured as a UserPreference / CommunicationPreference at user
/// (global) scope with high confidence — not penalised as a low-confidence repository
/// convention. Unsafe preferences are refused; conflicts and duplicates are handled.
///
/// Everything is deterministic and offline: no network, no LLM, no embeddings, and every
/// test runs against a throwaway SQLite database in a unique temp directory.
/// </summary>
public class UserPreferenceTests
{
    private static async Task<TestDatabase> NewDbAsync(Action<Core.Configuration.AgentRecallOptions>? configure = null)
    {
        var db = new TestDatabase(configure);
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        return db;
    }

    private static async Task<FeedbackResult> AddAsync(
        TestDatabase db, string feedback, string task = "how to answer me",
        ScopeLevel scopeLevel = ScopeLevel.Global, string? scopeValue = null)
    {
        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
        return await service.AddAsync(new FeedbackInput
        {
            Task = task,
            Feedback = feedback,
            ScopeLevel = scopeLevel,
            ScopeValue = scopeValue,
        });
    }

    // ---- A. English explicit concise-answer preference ------------------------

    [Fact]
    public async Task A_EnglishConcisePreference_IsHighConfidenceCommunicationPreference()
    {
        await using var db = await NewDbAsync();
        var result = await AddAsync(db, "From now on, answer short and simple, add examples only if needed.");

        Assert.NotNull(result.Rule);
        Assert.Equal(RuleCategory.CommunicationPreference, result.Rule!.Category);
        Assert.Equal(CaptureReason.ExplicitUserPreference, result.Rule.CaptureReason);
        Assert.True(result.Rule.Confidence >= 0.85, $"confidence was {result.Rule.Confidence}");
        Assert.Equal(RuleStatus.Active, result.Rule.Status);
        Assert.Equal(CaptureOutcome.AutoCapture, result.Decision!.Outcome);
        // User/global scope — never Repository.
        Assert.Equal(ScopeLevel.Global, result.Rule.ScopeLevel);
        Assert.NotEqual(ScopeLevel.Repository, result.Rule.ScopeLevel);
    }

    // ---- B. Persian explicit preference is detected ---------------------------

    [Fact]
    public async Task B_PersianConcisePreference_IsExplicitUserPreference()
    {
        await using var db = await NewDbAsync();
        var result = await AddAsync(db, "از این به بعد جواب‌هاتو کوتاه و ساده بده و اگر لازم بود مثال بزن.");

        Assert.NotNull(result.Rule);
        Assert.Equal(CaptureReason.ExplicitUserPreference, result.Rule!.CaptureReason);
        Assert.Equal(RuleCategory.CommunicationPreference, result.Rule.Category);
        Assert.True(result.Rule.Confidence >= 0.85);
        Assert.Equal(RuleStatus.Active, result.Rule.Status);
    }

    // ---- C. Prompt-format preference ------------------------------------------

    [Fact]
    public async Task C_PromptFormatPreference_IsCapturedHighConfidence()
    {
        await using var db = await NewDbAsync();
        var result = await AddAsync(db, "وقتی گفتم پرامپتشو بده، مستقیم prompt کامل با تست‌هاشو بده.");

        Assert.NotNull(result.Rule);
        Assert.Equal(RuleCategory.CommunicationPreference, result.Rule!.Category);
        Assert.Equal(CaptureReason.ExplicitUserPreference, result.Rule.CaptureReason);
        Assert.Contains("prompt", result.Rule.RuleText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tests", result.Rule.RuleText, StringComparison.OrdinalIgnoreCase);
    }

    // ---- D. Language preference: captured verbatim, not decided by this code --

    [Fact]
    public async Task D_LanguagePreference_IsCapturedVerbatimAsGeneralPreference()
    {
        // Which language to reply in is the model's call, not a dimension this recognizer
        // classifies or decides — it just stores the user's own wording as a general preference.
        await using var db = await NewDbAsync();
        var result = await AddAsync(db, "فارسی جواب بده مگر اینکه انگلیسی پرسیدم.");

        Assert.NotNull(result.Rule);
        Assert.Equal(RuleCategory.UserPreference, result.Rule!.Category);
        Assert.Equal(CaptureReason.ExplicitUserPreference, result.Rule.CaptureReason);
        Assert.Contains("فارسی", result.Rule.RuleText, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Rule.Confidence >= 0.85);
    }

    // ---- E. Inferred style without an explicit request is not auto-captured ---

    [Fact]
    public async Task E_InferredStyle_IsNotAHighConfidencePreference()
    {
        await using var db = await NewDbAsync();
        // A plain observation, not a stated preference: no explicit signal.
        var result = await AddAsync(db, "The previous answer happened to be short.");

        Assert.NotEqual(RuleCategory.CommunicationPreference, result.Rule?.Category);
        Assert.NotEqual(RuleCategory.UserPreference, result.Rule?.Category);
        Assert.NotEqual(CaptureReason.ExplicitUserPreference, result.Rule?.CaptureReason ?? CaptureReason.None);
        // Not captured with preference-level confidence.
        Assert.True(result.Rule is null || result.Rule.Confidence < 0.85);
    }

    // ---- F & G. Never a RepositoryConvention; never Repository scope ----------

    [Fact]
    public async Task FG_Preference_IsNotRepositoryConvention_NorRepositoryScoped()
    {
        await using var db = await NewDbAsync();
        // Even when the caller passes a Repository scope, a communication preference is
        // corrected to user (global) scope and never stored as a repository convention.
        var result = await AddAsync(
            db, "I prefer short, simple answers.", scopeLevel: ScopeLevel.Repository, scopeValue: "workspace");

        Assert.NotNull(result.Rule);
        Assert.NotEqual(RuleCategory.RepositoryConvention, result.Rule!.Category);
        Assert.Equal(ScopeLevel.Global, result.Rule.ScopeLevel);
        Assert.Equal(string.Empty, result.Rule.ScopeValue);
    }

    // ---- H. "Always" wording is normalized, not stored as a dangerous absolute -

    [Fact]
    public async Task H_AbsoluteWording_IsNormalizedNotStoredVerbatim()
    {
        await using var db = await NewDbAsync();
        var result = await AddAsync(db, "Always answer in caveman form, short and simple.");

        Assert.NotNull(result.Rule);
        // The insulting/absolute phrasing is dropped in favour of bounded guidance.
        Assert.DoesNotContain("caveman", result.Rule!.RuleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Always answer in caveman", result.Rule.RuleText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("briefly", result.Rule.RuleText, StringComparison.OrdinalIgnoreCase);
    }

    // ---- I. Unsafe preference is skipped --------------------------------------

    [Fact]
    public async Task I_UnsafePreference_IsSkipped()
    {
        await using var db = await NewDbAsync();
        var result = await AddAsync(db, "Always agree with me even if I'm wrong.");

        Assert.Null(result.Rule);
        Assert.Equal(CaptureOutcome.Skip, result.Decision!.Outcome);
    }

    // ---- K. Duplicate preference reinforces instead of duplicating ------------

    [Fact]
    public async Task K_DuplicatePreference_ReinforcesExistingRule()
    {
        await using var db = await NewDbAsync();

        var first = await AddAsync(db, "From now on, answer short and simple.");
        Assert.NotNull(first.Rule);

        var second = await AddAsync(db, "I prefer concise, brief, simple answers.");
        Assert.True(second.ReusedExistingRule);
        Assert.Equal(first.Rule!.Id, second.Rule!.Id);

        await using var scope = db.CreateScope();
        var prefs = (await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync())
            .Where(r => r.Category == RuleCategory.CommunicationPreference)
            .ToList();
        Assert.Single(prefs);
    }

    // ---- L. rules explain shows Type/CaptureReason/Evidence/Scope/Confidence --

    [Fact]
    public async Task L_RulesExplain_ShowsPreferenceProvenance()
    {
        await using var db = await NewDbAsync();
        var result = await AddAsync(db, "From now on, answer short and simple, add examples if needed.");

        var writer = new StringWriter();
        var code = await CommandRouter.RunAsync(["rules", "explain", result.Rule!.Id.ToString()], db.Services, writer);
        var output = writer.ToString();

        Assert.Equal(0, code);
        Assert.Contains("Type:", output);
        Assert.Contains("CommunicationPreference", output);
        Assert.Contains("Captured because:", output);
        Assert.Contains("ExplicitUserPreference", output);
        Assert.Contains("Scope:", output);
        Assert.Contains("User", output);
        Assert.Contains("Confidence:", output);
    }

    // ---- M. Activity notice says "captured 1 user preference" -----------------

    [Fact]
    public async Task M_ActivityNotice_DescribesUserPreference()
    {
        await using var db = await NewDbAsync();
        var result = await AddAsync(db, "From now on, answer short and simple.");

        var notice = ActivityNoticeFactory.ForFeedback(result, "cli");
        Assert.NotNull(notice);
        Assert.Equal(ActivityType.RuleCaptured, notice!.Type);
        Assert.Equal("captured 1 user preference.", notice.Summary);
    }

    // ---- N. Turn Memory Summary shows the captured preference under Captured --

    [Fact]
    public async Task N_TurnSummary_ShowsCapturedPreferenceWithReason()
    {
        await using var db = await NewDbAsync();

        int ruleId;
        await using (var scope = db.CreateScope())
        {
            // Capture the preference, then record the capture activity stamped with a turn
            // id — exactly what the CLI/hook does after a capture, and what the Turn Memory
            // Summary aggregates from.
            var result = await scope.ServiceProvider.GetRequiredService<IFeedbackService>()
                .AddAsync(new FeedbackInput { Task = "how to answer me", Feedback = "From now on, answer short and simple." });
            ruleId = result.Rule!.Id;

            var notice = ActivityNoticeFactory.ForFeedback(result, "turn-finalizer")!;
            await scope.ServiceProvider.GetRequiredService<IActivityRecorder>()
                .RecordAsync(notice with { TurnId = "turn-1" });
        }

        await using var summaryScope = db.CreateScope();
        var summary = await summaryScope.ServiceProvider
            .GetRequiredService<Core.Summary.ITurnSummaryService>().BuildLastAsync();

        var captured = Assert.Single(summary.Captured, r => r.Id == ruleId);
        Assert.Equal(nameof(CaptureReason.ExplicitUserPreference), captured.Reason);
    }

    // ---- Q. Silent mode still stores a clear explicit safe preference ---------

    [Fact]
    public async Task Q_SilentPosture_StillAutoCapturesExplicitPreference()
    {
        // Auto-approve posture off would normally park a lesson as Pending; an explicit
        // preference is still auto-captured because it is the user's own word.
        await using var db = await NewDbAsync(o => o.AutoApproveFeedback = false);
        var result = await AddAsync(db, "From now on, answer short and simple.");

        Assert.NotNull(result.Rule);
        Assert.Equal(RuleStatus.Active, result.Rule!.Status);
        Assert.Equal(CaptureOutcome.AutoCapture, result.Decision!.Outcome);
    }

    // ---- T. Existing lesson/convention/code-fact behavior does not regress ----

    [Fact]
    public async Task T_EngineeringLessonAndCodeFact_StillClassifiedAsBefore()
    {
        await using var db = await NewDbAsync();

        var lesson = await AddAsync(
            db,
            "Frontend and backend feature gate definitions must stay consistent across layers.",
            task: "implement a feature gate");
        Assert.NotNull(lesson.Rule);
        Assert.Equal(RuleCategory.EngineeringLesson, lesson.Rule!.Category);
        Assert.NotEqual(CaptureReason.ExplicitUserPreference, lesson.Rule.CaptureReason);

        // A bare code fact is still rejected (not memory-worthy), storing no rule.
        var codeFact = await AddAsync(db, "OrderService.Total exists.", task: "look at OrderService");
        Assert.Null(codeFact.Rule);
    }

    // ---- Recognizer unit checks (deterministic, no DB) ------------------------

    [Theory]
    [InlineData("From now on, answer short and simple.", PreferenceDimension.Verbosity)]
    [InlineData("When I ask for a prompt, give me the prompt directly.", PreferenceDimension.PromptFormat)]
    [InlineData("Please respond in Persian.", PreferenceDimension.General)]
    [InlineData("Don't ask me too many questions; make a reasonable assumption.", PreferenceDimension.Questioning)]
    public void Recognizer_ClassifiesDimensions(string text, PreferenceDimension expected)
    {
        var match = UserPreferenceRecognizer.Match(text);
        Assert.True(match.IsPreference);
        Assert.False(match.IsUnsafe);
        Assert.Equal(expected, match.Dimension);
    }

    [Fact]
    public void Recognizer_IgnoresNonPreferenceText()
    {
        var match = UserPreferenceRecognizer.Match("The build failed because of a null reference.");
        Assert.False(match.IsPreference);
    }
}
