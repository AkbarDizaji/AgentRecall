using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Extraction;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for condition-based rule classification: separating code facts,
/// repository conventions, and engineering lessons, normalizing rules into a
/// When/Do/Avoid/Because shape, and surfacing them in that shape on retrieval.
/// </summary>
public class ConditionBasedClassificationTests
{
    private static readonly MemoryWorthinessClassifier Classifier = new();

    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static ExtractedRule Extract(string feedback, string task = "", string? scopeValue = null) =>
        StructuredRuleExtractor.Extract(new FeedbackInput
        {
            Task = task,
            Feedback = feedback,
            ScopeLevel = scopeValue is null ? ScopeLevel.Global : ScopeLevel.Repository,
            ScopeValue = scopeValue,
        });

    // A. Pure code fact is rejected.
    [Fact]
    public void A_PureCodeFact_IsRejected()
    {
        var result = Classifier.Classify("IVenueManagerLevel5 exposes IsEventsFeatureEnabled");

        Assert.Equal(RuleCategory.CodeFact, result.Category);
        Assert.Equal(MemoryDecision.Reject, result.Decision);
    }

    // B. A method recommendation without a condition is a convention to review, not a blind code fact.
    [Fact]
    public void B_MethodRecommendation_IsRepositoryConvention_StoreOrReview()
    {
        const string input = "Use IsEventsFeatureEnabled instead of IsVenueMigratedFor";

        var result = Classifier.Classify(input);
        Assert.Equal(RuleCategory.RepositoryConvention, result.Category);
        Assert.True(result.Decision is MemoryDecision.Store or MemoryDecision.NeedsReview);

        var extracted = Extract(input);
        Assert.Contains("Events", extracted.Trigger, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IsEventsFeatureEnabled", extracted.Do, StringComparison.Ordinal);
        Assert.Contains("IsVenueMigratedFor", extracted.DoNot, StringComparison.Ordinal);
    }

    // C. A repository convention with an explicit condition is stored with full structure.
    [Fact]
    public void C_RepositoryConvention_IsStored_WithConditionActionAvoid()
    {
        const string input = "When implementing Events backend gates, use IsEventsFeatureEnabled instead of IsVenueMigratedFor";

        var result = Classifier.Classify(input);
        Assert.Equal(RuleCategory.RepositoryConvention, result.Category);
        Assert.Equal(MemoryDecision.Store, result.Decision);

        var extracted = Extract(input);
        Assert.Equal("When implementing Events backend gates", extracted.Trigger);
        Assert.Contains("IsEventsFeatureEnabled", extracted.Do, StringComparison.Ordinal);
        Assert.Contains("IsVenueMigratedFor", extracted.DoNot, StringComparison.Ordinal);
    }

    // D. An engineering lesson is stored.
    [Fact]
    public void D_EngineeringLesson_IsStored()
    {
        var result = Classifier.Classify("Frontend and backend feature gate definitions must match");

        Assert.Equal(RuleCategory.EngineeringLesson, result.Category);
        Assert.Equal(MemoryDecision.Store, result.Decision);
    }

    // E. A Moq convention keeps its condition and action.
    [Fact]
    public void E_MoqConvention_HasConditionAndAction()
    {
        const string input = "When writing Moq tests, use It.IsAny<T>() for irrelevant arguments";

        var result = Classifier.Classify(input);
        Assert.True(result.Category is RuleCategory.RepositoryConvention or RuleCategory.EngineeringLesson);

        var extracted = Extract(input);
        Assert.Contains("Moq tests", extracted.Trigger, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("It.IsAny<T>()", extracted.Do, StringComparison.Ordinal);
    }

    // F. Do and Avoid are not duplicates; Avoid stays empty without a real anti-pattern.
    [Fact]
    public void F_DoAndAvoid_AreNotDuplicates()
    {
        var extracted = Extract("Use Result<T> for recoverable domain failures");

        Assert.False(string.IsNullOrWhiteSpace(extracted.Do));
        Assert.Equal(string.Empty, extracted.DoNot);
        Assert.NotEqual(extracted.Do, extracted.DoNot);
    }

    // G. Reason is never fabricated from the scope value.
    [Fact]
    public void G_Reason_IsNotScopeValue()
    {
        var extracted = Extract("Use tabs for indentation.", scopeValue: "Skedda");

        Assert.NotEqual("Skedda", extracted.Reason);
        Assert.Equal(string.Empty, extracted.Reason);
    }

    // H. Retrieved context is rendered in the conditional format.
    [Fact]
    public async Task H_InjectContext_UsesConditionalFormat()
    {
        await using var db = new TestDatabase();
        await Init(db);

        int id;
        await using (var scope = db.CreateScope())
        {
            var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            var rule = await rules.AddAsync(new RecallRule
            {
                Category = RuleCategory.RepositoryConvention,
                Trigger = "When implementing Events backend gates",
                RuleText = "Use IsEventsFeatureEnabled.",
                Mistake = "Avoid IsVenueMigratedFor.",
                TechnicalContext = "Backend and frontend gate definitions must match.",
                Confidence = 0.9,
                Status = RuleStatus.Promoted,
                ScopeLevel = ScopeLevel.Global,
            });
            id = rule.Id;
        }

        var output = new StringWriter();
        var exit = await CommandRouter.RunAsync(["inject-context", "fix backend event gate"], db.Services, output);
        var text = output.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("When implementing", text, StringComparison.Ordinal);
        Assert.Contains("Do:", text, StringComparison.Ordinal);
        Assert.Contains("Avoid:", text, StringComparison.Ordinal);
        Assert.Contains("Because:", text, StringComparison.Ordinal);
        Assert.Contains($"#{id}", text, StringComparison.Ordinal);
    }

    // I. A code fact captured through the feedback flow stores no rule.
    [Fact]
    public async Task I_CodeFactCapture_StoresNoRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var feedback = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
        var result = await feedback.AddAsync(new FeedbackInput
        {
            Task = "reviewing OrderService",
            Feedback = "OrderService calls IOrderRepository.",
        });

        Assert.Null(result.Rule);
        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        Assert.Empty(await rules.ListAsync());
    }

    // J. An accepted PR comment cannot bypass the classifier.
    [Fact]
    public async Task J_AcceptedCodeFact_IsNotStored()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<IPullRequestImportService>();
        var result = await importer.ImportAsync(
            [new PullRequestComment { Body = "Config is in appsettings.json." }],
            new PullRequestImportOptions { Accepted = true, ScopeLevel = ScopeLevel.Repository, ScopeValue = "repo" });

        Assert.Equal(0, result.RulesCreated);
        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        Assert.Empty(await rules.ListAsync());
    }

    // K. An accepted repository convention becomes an Active, categorized rule.
    [Fact]
    public async Task K_AcceptedRepositoryConvention_IsActiveAndCategorized()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<IPullRequestImportService>();
        var result = await importer.ImportAsync(
            [new PullRequestComment { Body = "When implementing Events backend gates, use IsEventsFeatureEnabled instead of IsVenueMigratedFor" }],
            new PullRequestImportOptions { Accepted = true, ScopeLevel = ScopeLevel.Repository, ScopeValue = "repo" });

        Assert.Equal(1, result.RulesCreated);
        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var rule = Assert.Single(await rules.ListAsync());
        Assert.Equal(RuleStatus.Active, rule.Status);
        Assert.Equal(RuleCategory.RepositoryConvention, rule.Category);
    }

    // L. Rules from earlier versions (category Unknown, no structured avoid) still surface.
    [Fact]
    public async Task L_LegacyRulesWithoutCategory_StillSurface()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using (var scope = db.CreateScope())
        {
            var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            await rules.AddAsync(new RecallRule
            {
                // Category defaults to Unknown — exactly how an older row reads back.
                Trigger = "When writing SQL",
                RuleText = "Always use parameterized queries.",
                Confidence = 0.7,
                Status = RuleStatus.Active,
                ScopeLevel = ScopeLevel.Global,
            });
        }

        await using (var scope = db.CreateScope())
        {
            var search = scope.ServiceProvider.GetRequiredService<IRecallSearchService>();
            var results = await search.SearchAsync("parameterized queries");
            Assert.Contains(results, r => r.Rule.RuleText.Contains("parameterized", StringComparison.OrdinalIgnoreCase));

            var context = scope.ServiceProvider.GetRequiredService<Core.Context.IContextInjectionService>();
            var built = await context.BuildContextAsync(new Core.Context.ContextRequest { Task = "writing SQL queries" });
            Assert.Contains(built.All, r => r.Rule.RuleText.Contains("parameterized", StringComparison.OrdinalIgnoreCase));
        }
    }

    // Determinism: classification of the same input never changes.
    [Fact]
    public void Classification_IsDeterministic()
    {
        string[] inputs =
        [
            "IVenueManagerLevel5 exposes IsEventsFeatureEnabled",
            "When implementing Events backend gates, use IsEventsFeatureEnabled instead of IsVenueMigratedFor",
            "Frontend and backend feature gate definitions must match",
        ];

        foreach (var input in inputs)
        {
            var a = Classifier.Classify(input);
            var b = Classifier.Classify(input);
            Assert.Equal(a.Category, b.Category);
            Assert.Equal(a.Decision, b.Decision);
        }
    }
}
