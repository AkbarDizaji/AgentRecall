using AgentRecall.Cli.Devcontainer;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Memory;
using AgentRecall.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for the "lessons, not facts" memory-quality policy: the deterministic
/// <see cref="MemoryWorthinessClassifier"/> and its integration into the capture
/// flows through <see cref="FeedbackService"/> and pull-request import.
/// </summary>
public class MemoryWorthinessTests
{
    private static readonly MemoryWorthinessClassifier Classifier = new();

    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    // ---- Classifier: low-value code facts are rejected -------------------------

    [Theory]
    [InlineData("Use IsEventsFeatureEnabled.")]
    [InlineData("IVenueManagerLevel5 has IsEventsFeatureEnabled.")]
    public void Classify_MethodExistenceOrBareRecommendation_IsRejected(string candidate)
    {
        var result = Classifier.Classify(candidate);

        Assert.Equal(MemoryWorthiness.NotWorthStoring, result.Verdict);
    }

    [Theory]
    [InlineData("The config is in appsettings.json.")]
    [InlineData("The MaxUploadSize config key exists.")]
    public void Classify_FileAndConfigFacts_AreRejected(string candidate)
    {
        var result = Classifier.Classify(candidate);

        Assert.Equal(MemoryWorthiness.NotWorthStoring, result.Verdict);
    }

    [Fact]
    public void Classify_ServiceCallFact_IsRejected()
    {
        var result = Classifier.Classify("OrderService calls IOrderRepository.");

        Assert.Equal(MemoryWorthiness.NotWorthStoring, result.Verdict);
    }

    [Fact]
    public void Classify_BareUseMethod_IsRejected()
    {
        // A bare "use X" recommendation with no broader rationale is a code fact.
        var result = Classifier.Classify("Use IsEventsFeatureEnabled.");

        Assert.Equal(MemoryWorthiness.NotWorthStoring, result.Verdict);
        Assert.True(result.Confidence > 0);
    }

    // ---- Classifier: specific facts that hint at a reusable pattern ------------

    [Fact]
    public void Classify_FeatureGateMethodSubstitution_NeedsReviewWithGeneralizedLesson()
    {
        var result = Classifier.Classify("Use IsEventsFeatureEnabled instead of IsVenueMigratedFor for Events.");

        Assert.Equal(MemoryWorthiness.NeedsReview, result.Verdict);
        Assert.NotNull(result.SuggestedGeneralizedLesson);
        Assert.Contains("feature gate", result.SuggestedGeneralizedLesson!, StringComparison.OrdinalIgnoreCase);
        // The generalized lesson must not echo the raw, specific method names.
        Assert.DoesNotContain("IsEventsFeatureEnabled", result.SuggestedGeneralizedLesson!, StringComparison.Ordinal);
    }

    // ---- Classifier: valuable engineering lessons are stored -------------------

    [Theory]
    [InlineData("When implementing feature gates, ensure frontend and backend use the same definition.")]
    [InlineData("If the frontend checks flag + limit, the backend must not check only the flag.")]
    [InlineData("When writing Moq tests, avoid mixing exact instances with matcher-based setups.")]
    [InlineData("When fixing authorization bugs, verify both middleware and handler-level checks.")]
    public void Classify_ReusableLessons_AreWorthStoring(string candidate)
    {
        var result = Classifier.Classify(candidate);

        Assert.Equal(MemoryWorthiness.WorthStoring, result.Verdict);
    }

    [Fact]
    public void Classify_IsDeterministic()
    {
        string[] candidates =
        [
            "Use IsEventsFeatureEnabled.",
            "Use IsEventsFeatureEnabled instead of IsVenueMigratedFor for Events.",
            "When implementing feature gates, ensure frontend and backend use the same definition.",
            "OrderService calls IOrderRepository.",
            "use parameterized queries",
        ];

        foreach (var candidate in candidates)
        {
            var a = Classifier.Classify(candidate);
            var b = Classifier.Classify(candidate);

            Assert.Equal(a.Verdict, b.Verdict);
            Assert.Equal(a.Reason, b.Reason);
            Assert.Equal(a.Confidence, b.Confidence);
            Assert.Equal(a.SuggestedGeneralizedLesson, b.SuggestedGeneralizedLesson);
        }
    }

    // ---- Configuration defaults protect memory quality -------------------------

    [Fact]
    public void Options_DefaultsProtectMemoryQuality()
    {
        var options = new Core.Configuration.AgentRecallOptions();

        Assert.True(options.MemoryWorthinessEnabled);
        Assert.False(options.StoreRejectedCandidates);
        Assert.False(options.AllowCodeFactsWhenAccepted);
    }

    // ---- Integration: capture flow rejects code facts --------------------------

    [Fact]
    public async Task AddFeedback_CodeFact_DoesNotCreateRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var result = await service.AddAsync(new FeedbackInput
        {
            Task = "events feature work",
            Feedback = "Use IsEventsFeatureEnabled.",
        });

        Assert.Null(result.Rule);
        Assert.False(result.RuleStored);
        Assert.Equal(MemoryWorthiness.NotWorthStoring, result.Worthiness!.Verdict);

        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        Assert.Empty(await rules.ListAsync());
    }

    [Fact]
    public async Task AddFeedback_CodeFact_StoresNoEventByDefault()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        await service.AddAsync(new FeedbackInput { Task = "t", Feedback = "Use IsEventsFeatureEnabled." });

        var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();
        Assert.Empty(await events.ListAsync());
    }

    [Fact]
    public async Task AddFeedback_CodeFact_StoresAuditEvent_WhenConfigured()
    {
        await using var db = new TestDatabase(o => o.StoreRejectedCandidates = true);
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var result = await service.AddAsync(new FeedbackInput { Task = "t", Feedback = "Use IsEventsFeatureEnabled." });

        Assert.Null(result.Rule);
        Assert.NotNull(result.Event);

        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();
        Assert.Empty(await rules.ListAsync());
        var recorded = Assert.Single(await events.ListAsync());
        Assert.Null(recorded.RuleId);
    }

    [Fact]
    public async Task AddFeedback_NeedsReview_StoresGeneralizedLessonNotRawFact()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var result = await service.AddAsync(new FeedbackInput
        {
            Task = "events feature work",
            Feedback = "Use IsEventsFeatureEnabled instead of IsVenueMigratedFor for Events.",
        });

        Assert.NotNull(result.Rule);
        Assert.Contains("feature gate", result.Rule!.RuleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsEventsFeatureEnabled", result.Rule.RuleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddFeedback_DisabledClassifier_StoresEverything()
    {
        await using var db = new TestDatabase(o => o.MemoryWorthinessEnabled = false);
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var result = await service.AddAsync(new FeedbackInput { Task = "t", Feedback = "Use IsEventsFeatureEnabled." });

        Assert.NotNull(result.Rule);
        Assert.Null(result.Worthiness);
    }

    [Fact]
    public async Task AddFeedback_AllowCodeFactsWhenAccepted_StoresAcceptedCodeFact()
    {
        await using var db = new TestDatabase(o => o.AllowCodeFactsWhenAccepted = true);
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        // accepted (AutoApprove = true) + the override → the code fact is stored Active.
        var result = await service.AddAsync(new FeedbackInput
        {
            Task = "t",
            Feedback = "Use IsEventsFeatureEnabled.",
            AutoApprove = true,
        });

        Assert.NotNull(result.Rule);
        Assert.Equal(RuleStatus.Active, result.Rule!.Status);
    }

    // ---- Integration: accepted PR comments do not bypass the classifier --------

    [Fact]
    public async Task Import_AcceptedCodeFactComment_DoesNotBypassClassifier()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<IPullRequestImportService>();

        var result = await importer.ImportAsync(
            [new PullRequestComment { Body = "Use IsEventsFeatureEnabled." }],
            new PullRequestImportOptions { Accepted = true });

        Assert.Equal(0, result.RulesCreated);
        Assert.Equal(1, result.Skipped);

        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        Assert.Empty(await rules.ListAsync());
    }

    [Fact]
    public async Task Import_AcceptedGeneralizableComment_BecomesActiveGeneralizedLesson()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<IPullRequestImportService>();

        var result = await importer.ImportAsync(
            [new PullRequestComment { Body = "Use IsEventsFeatureEnabled instead of IsVenueMigratedFor for Events." }],
            new PullRequestImportOptions { Accepted = true });

        Assert.Equal(1, result.RulesCreated);

        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var rule = Assert.Single(await rules.ListAsync());
        Assert.Equal(RuleStatus.Active, rule.Status);
        Assert.Contains("feature gate", rule.RuleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsEventsFeatureEnabled", rule.RuleText, StringComparison.Ordinal);
    }

    // ---- Scaffold guidance teaches "lessons, not facts" ------------------------

    [Fact]
    public void ClaudeMdGuidance_TeachesLessonsNotFacts()
    {
        var guidance = DevcontainerScaffolder.ClaudeMdGuidance;

        Assert.Contains("Store lessons, not facts", guidance, StringComparison.Ordinal);
        Assert.Contains("Is this a reusable lesson or merely a code fact?", guidance, StringComparison.Ordinal);
    }
}
