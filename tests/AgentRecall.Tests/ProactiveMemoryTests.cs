using System.Text.Json.Nodes;
using AgentRecall.Cli.Mcp.Tools;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class ProactiveMemoryTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task<int> Seed(
        TestDatabase db, string trigger, string ruleText, RuleStatus status,
        double confidence = 0.5, ScopeLevel scope = ScopeLevel.Global, string scopeValue = "", string tags = "")
    {
        await using var s = db.CreateScope();
        var repo = s.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var rule = await repo.AddAsync(new RecallRule
        {
            Trigger = trigger, RuleText = ruleText, Mistake = "", TechnicalContext = "", Tags = tags,
            Confidence = confidence, Status = status, ScopeLevel = scope, ScopeValue = scopeValue,
        });
        return rule.Id;
    }

    // --- get_relevant_context ---------------------------------------------

    [Fact]
    public async Task GetRelevantContext_ReturnsMatchingRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "authentication middleware", "Validate tokens before the request pipeline.", RuleStatus.Promoted, tags: "auth");
        await Seed(db, "date formatting", "Use ISO 8601.", RuleStatus.Promoted);

        var tool = new GetRelevantContextTool();
        await using var scope = db.CreateScope();

        var result = await tool.InvokeAsync(
            new JsonObject { ["task"] = "implement authentication middleware" }, scope.ServiceProvider, CancellationToken.None);

        Assert.True(result["count"]!.GetValue<int>() >= 1);
        var rules = result["rules"]!.AsArray();
        Assert.Contains(rules, r => r!["rule"]!.GetValue<string>().Contains("tokens"));
    }

    [Fact]
    public async Task GetRelevantContext_ReturnsEmptyWhenNothingRelevant()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "date formatting", "Use ISO 8601.", RuleStatus.Promoted);

        var tool = new GetRelevantContextTool();
        await using var scope = db.CreateScope();

        var result = await tool.InvokeAsync(
            new JsonObject { ["task"] = "configure kubernetes networking" }, scope.ServiceProvider, CancellationToken.None);

        Assert.Equal(0, result["count"]!.GetValue<int>());
        Assert.Empty(result["rules"]!.AsArray());
    }

    // --- get_project_rules prioritization ---------------------------------

    [Fact]
    public async Task GetProjectRules_OrdersProjectThenPromotedThenActive()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var activeGlobal = await Seed(db, "g", "Active global rule.", RuleStatus.Active);
        var promotedGlobal = await Seed(db, "p", "Promoted global rule.", RuleStatus.Promoted);
        var projectRule = await Seed(db, "proj", "Project rule.", RuleStatus.Active, scope: ScopeLevel.Repository, scopeValue: "my-repo");

        var tool = new GetProjectRulesTool();
        await using var scope = db.CreateScope();

        var result = await tool.InvokeAsync(
            new JsonObject { ["scope_level"] = "Repository", ["scope_value"] = "my-repo" },
            scope.ServiceProvider, CancellationToken.None);

        var rules = result["rules"]!.AsArray();
        Assert.Equal(3, rules.Count);
        // Project first, then promoted, then active.
        Assert.Equal("Project rule.", rules[0]!["rule"]!.GetValue<string>());
        Assert.Equal("Promoted global rule.", rules[1]!["rule"]!.GetValue<string>());
        Assert.Equal("Active global rule.", rules[2]!["rule"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetProjectRules_ExcludesActiveRulesFromOtherProjects()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await Seed(db, "mine", "Mine.", RuleStatus.Active, scope: ScopeLevel.Repository, scopeValue: "my-repo");
        await Seed(db, "other", "Other.", RuleStatus.Active, scope: ScopeLevel.Repository, scopeValue: "other-repo");

        var tool = new GetProjectRulesTool();
        await using var scope = db.CreateScope();

        var result = await tool.InvokeAsync(
            new JsonObject { ["scope_level"] = "Repository", ["scope_value"] = "my-repo" },
            scope.ServiceProvider, CancellationToken.None);

        var rules = result["rules"]!.AsArray();
        Assert.Single(rules);
        Assert.Equal("Mine.", rules[0]!["rule"]!.GetValue<string>());
    }

    // --- suggest_feedback_candidate ---------------------------------------

    [Fact]
    public async Task SuggestFeedbackCandidate_DetectsCorrectionAndPrefersPositiveSentence()
    {
        await using var db = new TestDatabase();
        var tool = new SuggestFeedbackCandidateTool();
        await using var scope = db.CreateScope();

        var result = await tool.InvokeAsync(
            new JsonObject { ["conversation_message"] = "Don't use string interpolation in SQL. Use parameterized queries." },
            scope.ServiceProvider, CancellationToken.None);

        Assert.True(result["is_candidate"]!.GetValue<bool>());
        Assert.Equal("Use parameterized queries.", result["suggested_rule"]!.GetValue<string>());
    }

    [Fact]
    public async Task SuggestFeedbackCandidate_IgnoresNonCorrections()
    {
        await using var db = new TestDatabase();
        var tool = new SuggestFeedbackCandidateTool();
        await using var scope = db.CreateScope();

        var result = await tool.InvokeAsync(
            new JsonObject { ["conversation_message"] = "Thanks, that looks great!" },
            scope.ServiceProvider, CancellationToken.None);

        Assert.False(result["is_candidate"]!.GetValue<bool>());
    }

    // --- capture_feedback -------------------------------------------------

    [Fact]
    public async Task CaptureFeedback_CreatesEventAndPendingRuleInOneCall()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var tool = new CaptureFeedbackTool();
        JsonNode result;
        await using (var scope = db.CreateScope())
        {
            result = await tool.InvokeAsync(
                new JsonObject { ["feedback"] = "Use ConfigureAwait(false) in library code." },
                scope.ServiceProvider, CancellationToken.None);
        }

        Assert.True(result["event_id"]!.GetValue<int>() > 0);
        Assert.True(result["rule_id"]!.GetValue<int>() > 0);
        Assert.Equal("Pending", result["status"]!.GetValue<string>());

        await using (var scope = db.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();
            var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            Assert.Single(await events.ListAsync());
            var all = await rules.ListAsync();
            Assert.Single(all);
            Assert.Equal(RuleStatus.Pending, all[0].Status);
        }
    }

    // --- get_reminders ----------------------------------------------------

    [Fact]
    public async Task GetReminders_ReturnsOnlyPromotedOrHighConfidenceRules()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await Seed(db, "sql", "Check for SQL injection risks.", RuleStatus.Promoted, tags: "review");
        await Seed(db, "async", "Prefer async APIs.", RuleStatus.Active, confidence: 0.9, tags: "review");
        await Seed(db, "pending", "Low-confidence pending idea.", RuleStatus.Pending, confidence: 0.5, tags: "review");

        var tool = new GetRemindersTool();
        await using var scope = db.CreateScope();

        var result = await tool.InvokeAsync(
            new JsonObject { ["task_type"] = "code_review" }, scope.ServiceProvider, CancellationToken.None);

        var reminders = result["reminders"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(2, reminders.Count);
        Assert.Contains(reminders, r => r.Contains("SQL injection"));
        Assert.Contains(reminders, r => r.Contains("async"));
        Assert.DoesNotContain(reminders, r => r.Contains("Low-confidence"));
    }

    // --- analyzer unit ----------------------------------------------------

    [Theory]
    [InlineData("Don't load entire tables. Filter in the database.", true)]
    [InlineData("Always validate null handling.", true)]
    [InlineData("Looks good to me.", false)]
    public void Analyzer_ClassifiesMessages(string message, bool expected)
    {
        var analyzer = new FeedbackCandidateAnalyzer();
        Assert.Equal(expected, analyzer.Analyze(message).IsCandidate);
    }
}
