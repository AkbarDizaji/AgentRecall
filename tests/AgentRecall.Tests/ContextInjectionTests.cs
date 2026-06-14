using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class ContextInjectionTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task<int> Seed(
        TestDatabase db,
        string ruleText,
        string tags = "",
        string trigger = "",
        double confidence = 0.6,
        RuleStatus status = RuleStatus.Active,
        ScopeLevel scopeLevel = ScopeLevel.Global,
        string scopeValue = "",
        string mistake = "")
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var rule = await repo.AddAsync(new RecallRule
        {
            Trigger = trigger, RuleText = ruleText, Mistake = mistake, TechnicalContext = "",
            Tags = tags, Confidence = confidence, Status = status,
            ScopeLevel = scopeLevel, ScopeValue = scopeValue,
        });
        return rule.Id;
    }

    private static async Task<ContextInjectionResult> Build(TestDatabase db, ContextRequest request)
    {
        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IContextInjectionService>();
        return await service.BuildContextAsync(request);
    }

    // ---- Semantic similarity --------------------------------------------------

    [Fact]
    public async Task RetrievesRuleBySemanticRelation_NotJustKeyword()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // The Money rule never mentions "refund", yet the task is about refunds.
        var moneyId = await Seed(db,
            "Always use a Money value object holding amount and currency together.",
            tags: "money,domain-modeling,value-object", confidence: 0.9, status: RuleStatus.Promoted);
        await Seed(db, "Format dates as ISO 8601.", tags: "dates", confidence: 0.9, status: RuleStatus.Promoted);

        var result = await Build(db, new ContextRequest { Task = "Add refund support" });

        // Retrieved by meaning, and ranked top.
        Assert.Contains(result.All, r => r.Rule.Id == moneyId);
        Assert.Equal(moneyId, result.All.First().Rule.Id);

        var money = result.All.First(r => r.Rule.Id == moneyId);
        Assert.Equal(RuleImportance.MustFollow, money.Importance);
        Assert.Contains("money", money.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refund", money.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DoesNotRetrieveUnrelatedRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Format dates as ISO 8601.", tags: "dates");

        var result = await Build(db, new ContextRequest { Task = "Add refund support" });

        Assert.Empty(result.All);
    }

    // ---- Domain matches (files / changed entities) ----------------------------

    [Fact]
    public async Task MatchesOnChangedEntitiesAndFileNames()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var moneyId = await Seed(db,
            "Use a Money value object for amounts and currency.",
            tags: "money", confidence: 0.7);

        // Task text is generic; the signal comes from the changed code.
        var result = await Build(db, new ContextRequest
        {
            Task = "Update the service",
            FileNames = ["PaymentService.cs"],
            ChangedEntities = ["InvoiceLineItem", "Refund"],
        });

        var money = Assert.Single(result.All, r => r.Rule.Id == moneyId);
        Assert.Contains("money", money.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirectEntityTokenMatchIsExplained()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await Seed(db, "Validate currency codes against ISO 4217.", tags: "currency");

        var result = await Build(db, new ContextRequest
        {
            Task = "tweak code",
            ChangedEntities = ["CurrencyConverter"],
        });

        var rule = Assert.Single(result.All, r => r.Rule.Id == id);
        Assert.Contains("currency", rule.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Task-type matches ----------------------------------------------------

    [Fact]
    public async Task TaskTypeBoostsAlignedRule()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var securityId = await Seed(db,
            "Sanitize all user input before use.", tags: "security,validation", confidence: 0.6);
        var styleId = await Seed(db,
            "Sanitize file names for readability.", tags: "style", confidence: 0.6);

        var result = await Build(db, new ContextRequest
        {
            Task = "sanitize incoming data",
            TaskType = TaskType.Security,
        });

        // Both mention "sanitize", but the security task lifts the security rule.
        var security = result.All.First(r => r.Rule.Id == securityId);
        var style = result.All.First(r => r.Rule.Id == styleId);
        Assert.True(security.Score > style.Score);
        Assert.Contains("Security", security.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Ranking quality ------------------------------------------------------

    [Fact]
    public async Task RanksByUsefulness_ConfidenceAndScope()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // All three are about SQL; usefulness should order them.
        var promoted = await Seed(db, "Use parameterized SQL queries.", tags: "sql",
            confidence: 0.95, status: RuleStatus.Promoted);
        var project = await Seed(db, "Use the project SQL helper for queries.", tags: "sql",
            confidence: 0.6, scopeLevel: ScopeLevel.Repository, scopeValue: "AgentRecall");
        var weak = await Seed(db, "Consider indexing SQL queries.", tags: "sql", confidence: 0.3);

        var result = await Build(db, new ContextRequest
        {
            Task = "write SQL queries",
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "AgentRecall",
        });

        var order = result.All.Select(r => r.Rule.Id).ToList();
        // The weak, low-confidence global rule ranks last.
        Assert.Equal(weak, order.Last());
        Assert.Contains(promoted, order);
        Assert.Contains(project, order);

        // Scores are monotonically non-increasing across the combined ranking.
        var scores = result.All.Select(r => r.Score).ToList();
        Assert.Equal(scores.OrderByDescending(s => s), scores);
    }

    [Fact]
    public async Task ProhibitionRuleBecomesWarning()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Never concatenate SQL strings.", tags: "sql,security", confidence: 0.9);

        var result = await Build(db, new ContextRequest { Task = "build a SQL query" });

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("Never", warning.Rule.RuleText);
    }

    // ---- Policy integration & token budget ------------------------------------

    [Fact]
    public async Task ConflictingRulesArePrunedByPolicyEngine()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Use the repository pattern.", tags: "repository", confidence: 0.5);
        await Seed(db, "Do not use the repository pattern.", tags: "repository",
            confidence: 0.9, status: RuleStatus.Promoted);

        var result = await Build(db, new ContextRequest { Task = "structure repository data access" });

        // The conflict is resolved: only the winner survives.
        Assert.Single(result.All);
        Assert.Contains("set aside", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RespectsTokenBudget()
    {
        await using var db = new TestDatabase();
        await Init(db);
        for (var i = 0; i < 10; i++)
        {
            await Seed(db, $"Use parameterized SQL queries, variation {i} with extra descriptive text.",
                tags: "sql", confidence: 0.7);
        }

        var result = await Build(db, new ContextRequest
        {
            Task = "write SQL queries",
            TokenBudget = 60,
        });

        Assert.True(result.TokensUsed <= 60);
        Assert.NotEmpty(result.All); // at least the top rule fits
        Assert.Contains("budget", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // ---- MCP tool -------------------------------------------------------------

    [Fact]
    public void Server_RegistersInjectContextTool()
    {
        var names = AgentRecall.Cli.Mcp.McpServer.DefaultTools().Select(t => t.Name).ToHashSet();
        Assert.Contains("inject_context", names);
    }

    [Fact]
    public async Task InjectContextTool_ReturnsBucketedRulesWithExplanations()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Always use a Money value object holding amount and currency.",
            tags: "money", confidence: 0.9, status: RuleStatus.Promoted);

        var tool = new AgentRecall.Cli.Mcp.Tools.InjectContextTool();
        await using var scope = db.CreateScope();

        var args = new JsonObject
        {
            ["task"] = "add refund support",
            ["changed_entities"] = new JsonArray { "Refund" },
        };
        var result = await tool.InvokeAsync(args, scope.ServiceProvider, CancellationToken.None);

        var mustFollow = result["must_follow"]!.AsArray();
        Assert.Single(mustFollow);
        Assert.False(string.IsNullOrWhiteSpace(mustFollow[0]!["explanation"]!.GetValue<string>()));
        Assert.NotNull(mustFollow[0]!["match_reasons"]);
        Assert.True(result["tokens_used"]!.GetValue<int>() > 0);
    }
}
