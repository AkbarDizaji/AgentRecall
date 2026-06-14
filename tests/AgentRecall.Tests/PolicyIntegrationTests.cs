using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Policy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class PolicyIntegrationTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task<int> Seed(TestDatabase db, RecallRule rule)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        return (await repo.AddAsync(rule)).Id;
    }

    [Fact]
    public async Task ResolveForTask_MatchesAndResolvesConflict()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await Seed(db, new RecallRule
        {
            Trigger = "data access", RuleText = "Use the repository pattern.",
            Mistake = "", TechnicalContext = "", Tags = "repository",
            Confidence = 0.5, Status = RuleStatus.Active,
        });
        // Newer conflicting rule — should win on recency.
        await Seed(db, new RecallRule
        {
            Trigger = "data access", RuleText = "Do not use the repository pattern.",
            Mistake = "", TechnicalContext = "", Tags = "repository",
            Confidence = 0.5, Status = RuleStatus.Active,
        });
        // Unrelated rule — must not be matched.
        await Seed(db, new RecallRule
        {
            Trigger = "dates", RuleText = "Use ISO 8601 for timestamps.",
            Mistake = "", TechnicalContext = "", Tags = "dates",
            Confidence = 0.5, Status = RuleStatus.Active,
        });

        await using var scope = db.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IPolicyEngine>();

        var result = await engine.ResolveForTaskAsync(
            "decide how to structure repository data access", PolicyContext.None);

        // Only the two repository-pattern rules match; one wins, one is ignored.
        Assert.Single(result.Conflicts);
        Assert.Single(result.Effective);
        Assert.Single(result.Ignored);
        Assert.Contains("repository", result.Conflicts[0].Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Server_RegistersResolveRulesTool()
    {
        var names = AgentRecall.Cli.Mcp.McpServer.DefaultTools().Select(t => t.Name).ToHashSet();
        Assert.Contains("resolve_rules", names);
    }

    [Fact]
    public async Task ResolveRulesTool_ReturnsEffectiveIgnoredAndExplanation()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await Seed(db, new RecallRule
        {
            Trigger = "data access", RuleText = "Use the repository pattern.",
            Mistake = "", TechnicalContext = "", Tags = "repository",
            Confidence = 0.5, Status = RuleStatus.Active,
        });
        await Seed(db, new RecallRule
        {
            Trigger = "data access", RuleText = "Do not use the repository pattern.",
            Mistake = "", TechnicalContext = "", Tags = "repository",
            Confidence = 0.5, Status = RuleStatus.Active, Priority = 5,
        });

        var tool = new AgentRecall.Cli.Mcp.Tools.ResolveRulesTool();
        await using var scope = db.CreateScope();

        var args = new JsonObject { ["task"] = "structure repository data access" };
        var result = await tool.InvokeAsync(args, scope.ServiceProvider, CancellationToken.None);

        Assert.Equal(1, result["effective_count"]!.GetValue<int>());
        Assert.Equal(1, result["ignored_count"]!.GetValue<int>());

        var conflicts = result["conflicts"]!.AsArray();
        Assert.Single(conflicts);

        // The higher-priority "do not" rule wins.
        var effective = result["effective"]!.AsArray();
        Assert.Contains("Do not", effective[0]!["rule"]!.GetValue<string>());
        Assert.NotNull(effective[0]!["reason"]);
        Assert.False(string.IsNullOrWhiteSpace(result["explanation"]!.GetValue<string>()));
    }
}
