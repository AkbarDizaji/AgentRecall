using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class PersistenceTests
{
    [Fact]
    public async Task Initialize_CreatesDatabaseFile()
    {
        await using var db = new TestDatabase();

        await using var scope = db.CreateScope();
        var initializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();

        var path = await initializer.InitializeAsync();

        Assert.Equal(db.Options.DatabasePath, path);
        Assert.True(File.Exists(path), "Expected the SQLite database file to be created.");
    }

    [Fact]
    public async Task RecallRule_AddGetList_RoundTrips()
    {
        await using var db = new TestDatabase();
        await InitializeAsync(db);

        int id;
        await using (var scope = db.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();

            var added = await repo.AddAsync(new RecallRule
            {
                Trigger = "writing a SQL query with string concatenation",
                Mistake = "introduced a SQL injection",
                RuleText = "Always use parameterized queries.",
                TechnicalContext = "EF Core / ADO.NET",
                Tags = "security,sql",
                Confidence = 0.9,
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "AgentRecall",
                Status = RuleStatus.Active,
            });

            Assert.True(added.Id > 0);
            Assert.Equal(1, added.Version);
            Assert.NotEqual(default, added.CreatedAt);
            Assert.NotEqual(default, added.UpdatedAt);
            id = added.Id;
        }

        // Fresh scope to confirm it persisted, not just cached in-context.
        await using (var scope = db.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();

            var fetched = await repo.GetAsync(id);
            Assert.NotNull(fetched);
            Assert.Equal("Always use parameterized queries.", fetched!.RuleText);
            Assert.Equal(ScopeLevel.Repository, fetched.ScopeLevel);
            Assert.Equal(RuleStatus.Active, fetched.Status);

            var all = await repo.ListAsync();
            Assert.Single(all);
        }
    }

    [Fact]
    public async Task RecallEvent_And_RecallScope_CanBeStored()
    {
        await using var db = new TestDatabase();
        await InitializeAsync(db);

        await using var scope = db.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();
        var scopes = scope.ServiceProvider.GetRequiredService<IRecallScopeRepository>();

        var ev = await events.AddAsync(new RecallEvent
        {
            Type = RecallEventType.MistakeObserved,
            Trigger = "code review",
            Details = "Observed an unparameterized query.",
        });
        Assert.True(ev.Id > 0);
        Assert.NotEqual(default, ev.CreatedAt);

        var sc = await scopes.AddAsync(new RecallScope
        {
            Level = ScopeLevel.Repository,
            Value = "AgentRecall",
            Description = "The AgentRecall repo.",
        });
        Assert.True(sc.Id > 0);

        Assert.Single(await events.ListAsync());
        Assert.Single(await scopes.ListAsync());
    }

    [Fact]
    public async Task UpdateAsync_StampsUpdatedAt_WithoutCallerSettingIt()
    {
        await using var db = new TestDatabase();
        await InitializeAsync(db);

        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();

        var added = await repo.AddAsync(new RecallRule
        {
            Trigger = "t", RuleText = "r", Status = RuleStatus.Pending, Confidence = 0.5,
        });
        var firstStamp = added.UpdatedAt;

        // Mutate without touching UpdatedAt; the OnUpdating hook must stamp it.
        await Task.Delay(5);
        added.Status = RuleStatus.Active;
        var updated = await repo.UpdateAsync(added);

        Assert.True(updated.UpdatedAt > firstStamp, "Expected the OnUpdating hook to stamp UpdatedAt.");
    }

    [Fact]
    public async Task DeleteAsync_RemovesEntity_AndReportsMissing()
    {
        await using var db = new TestDatabase();
        await InitializeAsync(db);

        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();

        var added = await repo.AddAsync(new RecallRule { Trigger = "t", RuleText = "r", Status = RuleStatus.Active });

        Assert.True(await repo.DeleteAsync(added.Id));
        Assert.Null(await repo.GetAsync(added.Id));
        Assert.Empty(await repo.ListAsync());
        // Deleting a missing row is a no-op that reports false rather than throwing.
        Assert.False(await repo.DeleteAsync(added.Id));
    }

    [Fact]
    public async Task QueryAsync_FiltersByScopeAndStatus_AtDbLayer()
    {
        await using var db = new TestDatabase();
        await InitializeAsync(db);

        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();

        await repo.AddAsync(new RecallRule { Trigger = "t", RuleText = "a", Status = RuleStatus.Active, ScopeLevel = ScopeLevel.Repository, ScopeValue = "Skedda" });
        await repo.AddAsync(new RecallRule { Trigger = "t", RuleText = "b", Status = RuleStatus.Archived, ScopeLevel = ScopeLevel.Repository, ScopeValue = "skedda" });
        await repo.AddAsync(new RecallRule { Trigger = "t", RuleText = "c", Status = RuleStatus.Active, ScopeLevel = ScopeLevel.Repository, ScopeValue = "other" });
        await repo.AddAsync(new RecallRule { Trigger = "t", RuleText = "d", Status = RuleStatus.Active, ScopeLevel = ScopeLevel.Global, ScopeValue = "" });

        // Case-insensitive scope match, excluding Archived.
        var skedda = await repo.QueryAsync(new RuleQuery
        {
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "SKEDDA",
            ExcludeStatuses = [RuleStatus.Archived],
        });
        Assert.Equal(["a"], skedda.Select(r => r.RuleText).OrderBy(x => x).ToArray());

        // Include-only filter by status.
        var active = await repo.QueryAsync(new RuleQuery { Statuses = [RuleStatus.Active] });
        Assert.Equal(["a", "c", "d"], active.Select(r => r.RuleText).OrderBy(x => x).ToArray());
    }

    private static async Task InitializeAsync(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        var initializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
        await initializer.InitializeAsync();
    }
}
