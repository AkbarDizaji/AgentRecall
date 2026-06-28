using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Search;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class SearchTests
{
    private static RecallRule Rule(string trigger, string ruleText, RuleStatus status, double confidence = 0.5, string tags = "")
        => new()
        {
            Trigger = trigger,
            Mistake = string.Empty,
            RuleText = ruleText,
            TechnicalContext = string.Empty,
            Tags = tags,
            Confidence = confidence,
            Status = status,
            ScopeLevel = ScopeLevel.Global,
            ScopeValue = string.Empty,
        };

    private static async Task<int> Seed(TestDatabase db, RecallRule rule)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var saved = await repo.AddAsync(rule);
        return saved.Id;
    }

    private static async Task<IReadOnlyList<SearchResult>> Search(TestDatabase db, string query, SearchOptions? options = null)
    {
        await using var scope = db.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<IRecallSearchService>();
        return await search.SearchAsync(query, options);
    }

    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    [Fact]
    public async Task Search_ReturnsRelevantRules_AndOmitsIrrelevant()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var sqlId = await Seed(db, Rule(
            "writing SQL queries",
            "Always use parameterized queries to avoid SQL injection.",
            RuleStatus.Promoted, tags: "sql,security"));
        await Seed(db, Rule(
            "formatting dates",
            "Use ISO 8601 for date formatting.",
            RuleStatus.Promoted, tags: "dates"));

        var results = await Search(db, "sql injection");

        Assert.Single(results);
        Assert.Equal(sqlId, results[0].Rule.Id);
    }

    [Fact]
    public async Task Search_RankingPrefersPromotedRules()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // Identical content and confidence; only status differs.
        var pendingId = await Seed(db, Rule("null handling", "Guard against null references.", RuleStatus.Pending));
        var promotedId = await Seed(db, Rule("null handling", "Guard against null references.", RuleStatus.Promoted));

        var results = await Search(db, "null references");

        Assert.Equal(2, results.Count);
        Assert.Equal(promotedId, results[0].Rule.Id);
        Assert.Equal(pendingId, results[1].Rule.Id);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task Search_IgnoresArchivedAndSupersededRules()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var activeId = await Seed(db, Rule("concurrency", "Use a lock around shared state.", RuleStatus.Active));
        await Seed(db, Rule("concurrency", "Use a lock around shared state.", RuleStatus.Archived));
        await Seed(db, Rule("concurrency", "Use a lock around shared state.", RuleStatus.Superseded));

        var results = await Search(db, "concurrency lock");

        Assert.Single(results);
        Assert.Equal(activeId, results[0].Rule.Id);
        Assert.DoesNotContain(results, r => r.Rule.Status is RuleStatus.Archived or RuleStatus.Superseded);
    }

    [Fact]
    public async Task Search_IgnoresRetiredRules()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var activeId = await Seed(db, Rule("concurrency", "Use a lock around shared state.", RuleStatus.Active));
        await Seed(db, Rule("concurrency", "Use a lock around shared state.", RuleStatus.Retired));

        var results = await Search(db, "concurrency lock");

        // Retired rules are dead and must be excluded consistently with Superseded/Archived.
        var result = Assert.Single(results);
        Assert.Equal(activeId, result.Rule.Id);
        Assert.DoesNotContain(results, r => r.Rule.Status == RuleStatus.Retired);
    }

    [Fact]
    public async Task Search_StillSurfacesDraftAndPending()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await Seed(db, Rule("null handling", "Guard against null references.", RuleStatus.Draft));
        await Seed(db, Rule("null handling", "Guard against null references.", RuleStatus.Pending));

        var results = await Search(db, "null references");

        // Draft and Pending are in-progress, not dead, so they remain searchable.
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Search_RespectsScopeFilter()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using (var scope = db.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            await repo.AddAsync(new RecallRule
            {
                Trigger = "testing", RuleText = "Mock external services in unit tests.", Mistake = "",
                TechnicalContext = "", Tags = "", Confidence = 0.5, Status = RuleStatus.Promoted,
                ScopeLevel = ScopeLevel.Repository, ScopeValue = "AgentRecall",
            });
            await repo.AddAsync(new RecallRule
            {
                Trigger = "testing", RuleText = "Mock external services in unit tests.", Mistake = "",
                TechnicalContext = "", Tags = "", Confidence = 0.5, Status = RuleStatus.Promoted,
                ScopeLevel = ScopeLevel.Repository, ScopeValue = "OtherRepo",
            });
        }

        var results = await Search(db, "mock tests", new SearchOptions
        {
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "AgentRecall",
        });

        Assert.Single(results);
        Assert.Equal("AgentRecall", results[0].Rule.ScopeValue);
    }

    [Fact]
    public async Task Search_MatchesWholeWords_NotSubstrings()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // The console rule should match; the invoice/domain rule must not, even
        // though its text contains "in" inside "domain"/"instead"/"Invoice".
        var consoleId = await Seed(db, Rule(
            "writing console output",
            "Always add ** in console.writeline.",
            RuleStatus.Active, tags: "log"));
        await Seed(db, Rule(
            "refactoring the invoice model",
            "Refactor the Invoice domain model to use a Money value object instead of separate decimals.",
            RuleStatus.Active, tags: "design"));

        var results = await Search(db, "what should add automaticly in console");

        Assert.Single(results);
        Assert.Equal(consoleId, results[0].Rule.Id);
    }

    [Fact]
    public async Task Search_SplitsOnPunctuation_SoDottedWordsAreFound()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var id = await Seed(db, Rule(
            "writing console output",
            "Always add ** in console.writeline.",
            RuleStatus.Active));

        // "writeline" only appears glued to "console" by a dot in the rule text.
        var results = await Search(db, "writeline");

        Assert.Single(results);
        Assert.Equal(id, results[0].Rule.Id);
    }
}
