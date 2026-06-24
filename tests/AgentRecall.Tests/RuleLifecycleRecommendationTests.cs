using System.Text.Json;
using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for automatic rule lifecycle management: deterministic, advisory
/// recommendations (promote/archive/supersede/review) that never mutate rules until
/// applied. Distinct from retrieval, reports, mining, and compression.
/// </summary>
public class RuleLifecycleRecommendationTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);

    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task<int> SeedRule(
        TestDatabase db,
        string ruleText = "When writing tests, use argument matchers consistently.",
        string trigger = "When writing tests",
        double confidence = 0.6,
        RuleStatus status = RuleStatus.Active,
        ScopeLevel scope = ScopeLevel.Global,
        string scopeValue = "",
        string mistake = "",
        RuleCategory category = RuleCategory.RepositoryConvention,
        DateTimeOffset? lastUsedAt = null,
        int? supersededById = null)
    {
        await using var s = db.CreateScope();
        var repo = s.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var rule = await repo.AddAsync(new RecallRule
        {
            Trigger = trigger, RuleText = ruleText, Mistake = mistake, Confidence = confidence,
            Status = status, ScopeLevel = scope, ScopeValue = scopeValue, Category = category,
            LastUsedAt = lastUsedAt, SupersededById = supersededById,
        });
        return rule.Id;
    }

    private static async Task SeedRetrievals(TestDatabase db, int ruleId, int count)
    {
        await using var s = db.CreateScope();
        var events = s.ServiceProvider.GetRequiredService<IRecallEventRepository>();
        for (var i = 0; i < count; i++)
            await events.AddAsync(new RecallEvent { Type = RecallEventType.RuleApplied, RuleId = ruleId, Trigger = "retrieval", Details = "r" });
    }

    private static async Task SeedOutcome(TestDatabase db, int ruleId, double delta)
    {
        await using var s = db.CreateScope();
        var outcomes = s.ServiceProvider.GetRequiredService<IRuleOutcomeRepository>();
        await outcomes.AddAsync(new RuleOutcome { RuleId = ruleId, Type = OutcomeType.UserRejected, ConfidenceDelta = delta, Reason = "x" });
    }

    private static async Task<IReadOnlyList<RuleLifecycleRecommendation>> Suggest(TestDatabase db)
    {
        await using var s = db.CreateScope();
        return await s.ServiceProvider.GetRequiredService<IRuleLifecycleRecommendationService>()
            .SuggestAsync(new RecommendationQuery { AsOf = AsOf });
    }

    // A. Active high-confidence frequently-retrieved rule → Promote.
    [Fact]
    public async Task A_StrongRule_GetsPromote()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, confidence: 0.91, status: RuleStatus.Active, lastUsedAt: AsOf);
        await SeedRetrievals(db, id, 6);

        var recs = await Suggest(db);

        var rec = Assert.Single(recs, r => r.RuleId == id);
        Assert.Equal(RecommendationType.Promote, rec.RecommendationType);
    }

    // B. Promoted stale low-confidence rule → Archive.
    [Fact]
    public async Task B_StaleLowConfidence_GetsArchive()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, confidence: 0.20, status: RuleStatus.Promoted, lastUsedAt: AsOf.AddDays(-200));

        var rec = Assert.Single(await Suggest(db), r => r.RuleId == id);
        Assert.Equal(RecommendationType.Archive, rec.RecommendationType);
    }

    // C. Superseded rule → Archive.
    [Fact]
    public async Task C_SupersededRule_GetsArchive()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, status: RuleStatus.Superseded, supersededById: 999, lastUsedAt: AsOf);

        var rec = Assert.Single(await Suggest(db), r => r.RuleId == id);
        Assert.Equal(RecommendationType.Archive, rec.RecommendationType);
    }

    // D. A similar but clearly stronger rule → Supersede.
    [Fact]
    public async Task D_StrongerSimilarRule_GetsSupersede()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var oldId = await SeedRule(db, ruleText: "Use IsVenueMigratedFor for Events.", trigger: "Events gating",
            confidence: 0.50, status: RuleStatus.Active, scope: ScopeLevel.Global, lastUsedAt: AsOf);
        var newId = await SeedRule(db, ruleText: "When implementing Events backend gates, use IsEventsFeatureEnabled instead of IsVenueMigratedFor.",
            trigger: "When implementing Events backend gates", mistake: "Avoid IsVenueMigratedFor.",
            confidence: 0.90, status: RuleStatus.Promoted, scope: ScopeLevel.Repository, scopeValue: "AgentRecall", lastUsedAt: AsOf);

        var recs = await Suggest(db);

        var rec = Assert.Single(recs, r => r.RecommendationType == RecommendationType.Supersede);
        Assert.Equal(oldId, rec.RuleId);
        Assert.Equal(newId, rec.TargetRuleId);
    }

    // E. Two comparable rules in an unresolved conflict → Review.
    [Fact]
    public async Task E_UnresolvedConflict_GetsReview()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedRule(db, ruleText: "Use the repository pattern.", trigger: "When accessing data", confidence: 0.6, lastUsedAt: AsOf);
        await SeedRule(db, ruleText: "Do not use the repository pattern.", trigger: "When accessing data", confidence: 0.6, lastUsedAt: AsOf);

        var recs = await Suggest(db);

        Assert.Contains(recs, r => r.RecommendationType == RecommendationType.Review);
        Assert.DoesNotContain(recs, r => r.RecommendationType == RecommendationType.Supersede);
    }

    // F. Low-confidence but frequently-retrieved rule → Review.
    [Fact]
    public async Task F_LowConfidenceFrequent_GetsReview()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, confidence: 0.30, status: RuleStatus.Active, lastUsedAt: AsOf);
        await SeedRetrievals(db, id, 6);

        var rec = Assert.Single(await Suggest(db), r => r.RuleId == id);
        Assert.Equal(RecommendationType.Review, rec.RecommendationType);
    }

    // G. Missing condition/action fields → Review.
    [Fact]
    public async Task G_MissingFields_GetsReview()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, ruleText: "", trigger: "When writing tests", status: RuleStatus.Active, lastUsedAt: AsOf);

        var rec = Assert.Single(await Suggest(db), r => r.RuleId == id);
        Assert.Equal(RecommendationType.Review, rec.RecommendationType);
    }

    // H. Suggesting is a dry run and never mutates rules.
    [Fact]
    public async Task H_Suggest_DoesNotMutateRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, confidence: 0.91, status: RuleStatus.Active, lastUsedAt: AsOf);
        await SeedRetrievals(db, id, 6);

        await Suggest(db);

        await using var s = db.CreateScope();
        var rule = await s.ServiceProvider.GetRequiredService<IRecallRuleRepository>().GetAsync(id);
        Assert.Equal(RuleStatus.Active, rule!.Status);
        Assert.Equal(0.91, rule.Confidence, 3);
    }

    // I. `lifecycle suggest --apply` applies safe recommendations (promote).
    [Fact]
    public async Task I_SuggestApply_AppliesSafeRecommendations()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, confidence: 0.91, status: RuleStatus.Active, lastUsedAt: DateTimeOffset.UtcNow);
        await SeedRetrievals(db, id, 6);

        var exit = await CommandRouter.RunAsync(["lifecycle", "suggest", "--apply"], db.Services, new StringWriter());
        Assert.Equal(0, exit);

        await using var s = db.CreateScope();
        var rule = await s.ServiceProvider.GetRequiredService<IRecallRuleRepository>().GetAsync(id);
        Assert.Equal(RuleStatus.Promoted, rule!.Status);
    }

    // J. Applying a Promote recommendation changes Active → Promoted.
    [Fact]
    public async Task J_ApplyPromote_ActiveToPromoted()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, confidence: 0.91, status: RuleStatus.Active, lastUsedAt: AsOf);
        await SeedRetrievals(db, id, 6);
        var recId = Assert.Single(await Suggest(db), r => r.RuleId == id).Id;

        await using var s = db.CreateScope();
        await s.ServiceProvider.GetRequiredService<IRuleLifecycleRecommendationService>().ApplyAsync(recId);

        var rule = await s.ServiceProvider.GetRequiredService<IRecallRuleRepository>().GetAsync(id);
        Assert.Equal(RuleStatus.Promoted, rule!.Status);
    }

    // K. Applying an Archive recommendation archives the rule.
    [Fact]
    public async Task K_ApplyArchive_ArchivesRule()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, confidence: 0.20, status: RuleStatus.Active, lastUsedAt: AsOf.AddDays(-200));
        var recId = Assert.Single(await Suggest(db), r => r.RuleId == id).Id;

        await using var s = db.CreateScope();
        await s.ServiceProvider.GetRequiredService<IRuleLifecycleRecommendationService>().ApplyAsync(recId);

        var rule = await s.ServiceProvider.GetRequiredService<IRecallRuleRepository>().GetAsync(id);
        Assert.Equal(RuleStatus.Archived, rule!.Status);
    }

    // L. Applying a Supersede recommendation sets SupersededById.
    [Fact]
    public async Task L_ApplySupersede_SetsSupersededBy()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var oldId = await SeedRule(db, ruleText: "Use IsVenueMigratedFor for Events.", trigger: "Events gating",
            confidence: 0.50, status: RuleStatus.Active, lastUsedAt: AsOf);
        var newId = await SeedRule(db, ruleText: "When implementing Events backend gates, use IsEventsFeatureEnabled instead of IsVenueMigratedFor.",
            trigger: "When implementing Events backend gates", mistake: "Avoid IsVenueMigratedFor.",
            confidence: 0.90, status: RuleStatus.Promoted, scope: ScopeLevel.Repository, scopeValue: "AgentRecall", lastUsedAt: AsOf);
        var rec = Assert.Single(await Suggest(db), r => r.RecommendationType == RecommendationType.Supersede);

        await using var s = db.CreateScope();
        await s.ServiceProvider.GetRequiredService<IRuleLifecycleRecommendationService>().ApplyAsync(rec.Id);

        var oldRule = await s.ServiceProvider.GetRequiredService<IRecallRuleRepository>().GetAsync(oldId);
        Assert.Equal(newId, oldRule!.SupersededById);
        Assert.Equal(RuleStatus.Superseded, oldRule.Status);
    }

    // M. A rejected recommendation is not recreated on the next run.
    [Fact]
    public async Task M_RejectedRecommendation_NotRecreated()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, confidence: 0.91, status: RuleStatus.Active, lastUsedAt: AsOf);
        await SeedRetrievals(db, id, 6);
        var recId = Assert.Single(await Suggest(db), r => r.RuleId == id).Id;

        await using (var s = db.CreateScope())
            await s.ServiceProvider.GetRequiredService<IRuleLifecycleRecommendationService>().RejectAsync(recId, "no");

        var second = await Suggest(db);
        Assert.DoesNotContain(second, r => r.RuleId == id);

        await using (var s = db.CreateScope())
        {
            var all = await s.ServiceProvider.GetRequiredService<IRuleLifecycleRecommendationRepository>().ListAsync();
            Assert.Single(all); // not duplicated
            Assert.Equal(RecommendationStatus.Rejected, all[0].Status);
        }
    }

    // N. Duplicate recommendations are not created across runs.
    [Fact]
    public async Task N_Recommendations_AreNotDuplicated()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, confidence: 0.91, status: RuleStatus.Active, lastUsedAt: AsOf);
        await SeedRetrievals(db, id, 6);

        await Suggest(db);
        await Suggest(db);

        await using var s = db.CreateScope();
        var all = await s.ServiceProvider.GetRequiredService<IRuleLifecycleRecommendationRepository>().ListAsync();
        Assert.Single(all);
    }

    // O. JSON output is valid.
    [Fact]
    public async Task O_SuggestJson_IsValid()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, confidence: 0.91, status: RuleStatus.Active, lastUsedAt: DateTimeOffset.UtcNow);
        await SeedRetrievals(db, id, 6);

        var output = new StringWriter();
        var exit = await CommandRouter.RunAsync(["lifecycle", "suggest", "--json"], db.Services, output);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal("Promote", doc.RootElement[0].GetProperty("type").GetString());
    }

    // P. Evidence is deterministic across runs.
    [Fact]
    public async Task P_Evidence_IsDeterministic()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, confidence: 0.91, status: RuleStatus.Active, lastUsedAt: AsOf);
        await SeedRetrievals(db, id, 6);

        var first = Assert.Single(await Suggest(db), r => r.RuleId == id).Evidence;
        var second = Assert.Single(await Suggest(db), r => r.RuleId == id).Evidence;
        Assert.Equal(first, second);
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
