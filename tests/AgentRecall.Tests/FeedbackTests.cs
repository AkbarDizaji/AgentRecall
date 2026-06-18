using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class FeedbackTests
{
    private static FeedbackInput SampleInput() => new()
    {
        Task = "write a SQL query for user lookup",
        Feedback = "use parameterized queries",
        BadOutput = "\"SELECT * FROM users WHERE name = '\" + name + \"'\"",
        FixedOutput = "a parameterized command with @name",
        ScopeLevel = ScopeLevel.Repository,
        ScopeValue = "AgentRecall",
        Tags = "security,sql",
    };

    [Fact]
    public async Task AddFeedback_CreatesRecallEvent()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using (var scope = db.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
            var result = await service.AddAsync(SampleInput());

            Assert.NotNull(result.Event);
            Assert.NotNull(result.Rule);
            Assert.True(result.Event.Id > 0);
            Assert.Equal(RecallEventType.MistakeObserved, result.Event.Type);
            Assert.Equal(result.Rule.Id, result.Event.RuleId);
        }

        await using (var scope = db.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();
            Assert.Single(await events.ListAsync());
        }
    }

    [Fact]
    public async Task AddFeedback_CreatesActiveRuleByDefault()
    {
        await using var db = new TestDatabase();
        await Init(db);

        int ruleId;
        await using (var scope = db.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
            var result = await service.AddAsync(SampleInput());

            // Capturing approves by default now.
            Assert.NotNull(result.Rule);
            Assert.Equal(RuleStatus.Active, result.Rule.Status);
            Assert.True(result.Rule.Id > 0);
            ruleId = result.Rule.Id;
        }

        await using (var scope = db.CreateScope())
        {
            var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            var stored = await rules.GetAsync(ruleId);

            Assert.NotNull(stored);
            Assert.Equal(RuleStatus.Active, stored!.Status);
            Assert.Equal(ScopeLevel.Repository, stored.ScopeLevel);
            Assert.Contains("parameterized queries", stored.RuleText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("security,sql", stored.Tags);
        }
    }

    [Fact]
    public async Task AddFeedback_AutoApproveFalse_KeepsRulePending()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var result = await service.AddAsync(SampleInput() with { AutoApprove = false });

        Assert.NotNull(result.Rule);
        Assert.Equal(RuleStatus.Pending, result.Rule.Status);
    }

    [Fact]
    public async Task AddFeedback_GlobalAutoApproveOff_KeepsRulePending()
    {
        await using var db = new TestDatabase(o => o.AutoApproveFeedback = false);
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var result = await service.AddAsync(SampleInput());

        Assert.NotNull(result.Rule);
        Assert.Equal(RuleStatus.Pending, result.Rule.Status);
    }

    [Fact]
    public async Task AddFeedback_WithoutBadOutput_LeavesMistakeEmpty()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var result = await service.AddAsync(new FeedbackInput
        {
            Task = "writing SQL",
            Feedback = "use parameterized queries",
        });

        // No bad output → no distinct mistake, so "do"/"do not" won't duplicate.
        Assert.NotNull(result.Rule);
        Assert.Equal(string.Empty, result.Rule.Mistake);
        Assert.Contains("parameterized queries", result.Rule.RuleText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RulesList_ShowsCreatedRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var addOutput = new StringWriter();
        var addCode = await CommandRouter.RunAsync(
            ["feedback", "add", "--task", "write a SQL query", "--feedback", "use parameterized queries", "--tags", "sql"],
            db.Services, addOutput);
        Assert.Equal(0, addCode);

        var listOutput = new StringWriter();
        var listCode = await CommandRouter.RunAsync(["rules", "list"], db.Services, listOutput);

        Assert.Equal(0, listCode);
        var text = listOutput.ToString();
        Assert.Contains("Active", text);
        // Trigger is normalized to a readable condition ("When writing a SQL query").
        Assert.Contains("SQL query", text);
    }

    [Fact]
    public async Task RulesShow_PrintsRuleDetail()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using (var scope = db.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
            await service.AddAsync(SampleInput());
        }

        var showOutput = new StringWriter();
        var code = await CommandRouter.RunAsync(["rules", "show", "1"], db.Services, showOutput);

        Assert.Equal(0, code);
        var text = showOutput.ToString();
        Assert.Contains("Rule #1", text);
        Assert.Contains("Active", text);
        Assert.Contains("parameterized queries", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RulesShow_MissingRule_ReturnsError()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["rules", "show", "999"], db.Services, output);

        Assert.Equal(1, code);
        Assert.Contains("not found", output.ToString());
    }

    [Fact]
    public async Task AddFeedback_DeduplicatesEquivalentRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var first = new FeedbackInput { Task = "logging", Feedback = "always add ** in console.writeline", Tags = "log" };
        // Different task and trailing punctuation, same guidance and scope.
        var again = new FeedbackInput { Task = "another logging task", Feedback = "Always add ** in console.writeline.", Tags = "log" };

        int firstRuleId;
        await using (var scope = db.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

            var r1 = await service.AddAsync(first);
            Assert.False(r1.ReusedExistingRule);
            Assert.NotNull(r1.Rule);
            Assert.NotNull(r1.Event);
            firstRuleId = r1.Rule.Id;

            var r2 = await service.AddAsync(again);
            Assert.True(r2.ReusedExistingRule);
            Assert.NotNull(r2.Rule);
            Assert.NotNull(r2.Event);
            Assert.Equal(firstRuleId, r2.Rule.Id);
            // The repeated feedback is still recorded as its own event.
            Assert.NotEqual(r1.Event.Id, r2.Event.Id);
        }

        await using (var scope = db.CreateScope())
        {
            var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();

            Assert.Single(await rules.ListAsync());
            Assert.Equal(2, (await events.ListAsync()).Count);
        }
    }

    [Fact]
    public async Task AddFeedback_DistinctGuidance_CreatesSeparateRules()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        var a = await service.AddAsync(new FeedbackInput { Task = "logging", Feedback = "always add ** in console.writeline" });
        var b = await service.AddAsync(new FeedbackInput { Task = "logging", Feedback = "prefer structured logging over console output" });

        Assert.False(b.ReusedExistingRule);
        Assert.NotNull(a.Rule);
        Assert.NotNull(b.Rule);
        Assert.NotEqual(a.Rule.Id, b.Rule.Id);
    }

    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }
}
