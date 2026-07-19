using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class LifecycleTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task<int> SeedRule(TestDatabase db, RuleStatus status, double confidence = 0.5, int version = 1, string trigger = "t")
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var rule = await repo.AddAsync(new RecallRule
        {
            Trigger = trigger, RuleText = "rule", Mistake = "", TechnicalContext = "", Tags = "",
            Confidence = confidence, Status = status, Version = version,
            ScopeLevel = ScopeLevel.Global, ScopeValue = "",
        });
        return rule.Id;
    }

    [Fact]
    public async Task Approve_MovesPendingToActive()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, RuleStatus.Pending);

        await using var scope = db.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();

        var updated = await lifecycle.ApproveAsync(id);

        Assert.Equal(RuleStatus.Active, updated.Status);
    }

    [Fact]
    public async Task Promote_MovesActiveToPromoted()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, RuleStatus.Active);

        await using var scope = db.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();

        var updated = await lifecycle.PromoteAsync(id);

        Assert.Equal(RuleStatus.Promoted, updated.Status);
    }

    [Fact]
    public async Task Approve_OnPromotedRule_Throws()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, RuleStatus.Promoted);

        await using var scope = db.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.ApproveAsync(id));
    }

    [Fact]
    public async Task Supersede_SetsRelationshipAndIncrementsVersion()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var oldId = await SeedRule(db, RuleStatus.Active, version: 1);
        var newId = await SeedRule(db, RuleStatus.Active, version: 1);

        await using (var scope = db.CreateScope())
        {
            var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();
            var result = await lifecycle.SupersedeAsync(oldId, newId);

            Assert.Equal(RuleStatus.Superseded, result.Superseded.Status);
            Assert.Equal(newId, result.Superseded.SupersededById);
            Assert.Equal(2, result.Replacement.Version);
        }

        // Verify it persisted and an event was recorded.
        await using (var scope = db.CreateScope())
        {
            var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();

            var oldRule = await rules.GetAsync(oldId);
            Assert.Equal(RuleStatus.Superseded, oldRule!.Status);
            Assert.Equal(newId, oldRule.SupersededById);

            var newRule = await rules.GetAsync(newId);
            Assert.Equal(2, newRule!.Version);

            var all = await events.ListAsync();
            Assert.Contains(all, e => e.Type == RecallEventType.RuleSuperseded && e.RuleId == oldId);
        }
    }

    [Fact]
    public async Task Reinforce_AutoPromotesWhenThresholdReached()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, RuleStatus.Active, confidence: 0.5);

        await using var scope = db.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();

        RecallRule updated = null!;
        for (var i = 0; i < 3; i++)
        {
            updated = await lifecycle.ReinforceAsync(id, RuleLifecycleService.ReinforcementDelta);
        }

        // 0.5 + 3 * 0.1 = 0.8 → promotion threshold reached.
        Assert.Equal(0.8, updated.Confidence, 3);
        Assert.Equal(RuleStatus.Promoted, updated.Status);
    }

    [Fact]
    public async Task Archive_SetsArchivedStatus()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, RuleStatus.Active);

        await using var scope = db.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();

        var updated = await lifecycle.ArchiveAsync(id);

        Assert.Equal(RuleStatus.Archived, updated.Status);
    }

    [Fact]
    public async Task Delete_OnDraftRule_RemovesRowAndRecordsEvent()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, RuleStatus.Draft);

        await using (var scope = db.CreateScope())
        {
            var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();
            var deleted = await lifecycle.DeleteAsync(id);
            Assert.Equal(RuleStatus.Draft, deleted.Status);
        }

        await using (var scope = db.CreateScope())
        {
            var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();

            Assert.Null(await rules.GetAsync(id));

            var all = await events.ListAsync();
            Assert.Contains(all, e => e.Type == RecallEventType.RuleDeleted && e.RuleId == id);
        }
    }

    [Fact]
    public async Task Delete_OnActiveRule_WithoutForce_Throws()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, RuleStatus.Active);

        await using var scope = db.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.DeleteAsync(id));

        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        Assert.NotNull(await rules.GetAsync(id));
    }

    [Fact]
    public async Task Delete_OnActiveRule_WithForce_Removes()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, RuleStatus.Active);

        await using var scope = db.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();

        await lifecycle.DeleteAsync(id, force: true);

        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        Assert.Null(await rules.GetAsync(id));
    }
}
