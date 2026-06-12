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

    private static async Task InitializeAsync(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        var initializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
        await initializer.InitializeAsync();
    }
}
