using System.Text.Json.Nodes;
using AgentRecall.Cli.Mcp;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class McpTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task SeedRule(TestDatabase db, string trigger, string ruleText, RuleStatus status)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        await repo.AddAsync(new RecallRule
        {
            Trigger = trigger, RuleText = ruleText, Mistake = "made a mistake",
            TechnicalContext = "", Tags = "", Confidence = 0.7,
            Status = status, ScopeLevel = ScopeLevel.Global, ScopeValue = "",
        });
    }

    [Fact]
    public void Server_RegistersExpectedTools()
    {
        var tools = McpServer.DefaultTools();
        var names = tools.Select(t => t.Name).ToHashSet();

        Assert.Contains("search_rules", names);
        Assert.Contains("add_feedback", names);
        Assert.Contains("get_project_rules", names);
        Assert.Contains("get_relevant_context", names);
        Assert.Contains("suggest_feedback_candidate", names);
        Assert.Contains("capture_feedback", names);
        Assert.Contains("get_reminders", names);

        // Every tool exposes a name, description and an object input schema.
        Assert.All(tools, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name));
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            Assert.Equal("object", t.InputSchema["type"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task ToolsList_OverJsonRpc_ReturnsAllTools()
    {
        await using var db = new TestDatabase();
        var server = new McpServer(db.Services);

        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        var response = await server.HandleMessageAsync(request, CancellationToken.None);

        Assert.NotNull(response);
        var tools = response!["result"]!["tools"]!.AsArray();
        Assert.Equal(McpServer.DefaultTools().Count, tools.Count);
    }

    [Fact]
    public async Task Initialize_ReturnsServerInfoAndCapabilities()
    {
        await using var db = new TestDatabase();
        var server = new McpServer(db.Services);

        var request = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""";
        var response = await server.HandleMessageAsync(request, CancellationToken.None);

        var result = response!["result"]!;
        Assert.Equal("2024-11-05", result["protocolVersion"]!.GetValue<string>());
        Assert.Equal("agentrecall", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task SearchRulesTool_ReturnsMatchingGuidance()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedRule(db, "writing SQL queries", "Use parameterized queries to avoid SQL injection.", RuleStatus.Promoted);
        await SeedRule(db, "formatting dates", "Use ISO 8601.", RuleStatus.Promoted);

        var tool = new Cli.Mcp.Tools.SearchRulesTool();
        await using var scope = db.CreateScope();

        var args = new JsonObject { ["query"] = "sql injection" };
        var result = await tool.InvokeAsync(args, scope.ServiceProvider, CancellationToken.None);

        Assert.Equal(1, result["count"]!.GetValue<int>());
        var rules = result["rules"]!.AsArray();
        Assert.Single(rules);

        var guidance = rules[0]!;
        // Guidance carries the agent-facing fields in snake_case.
        Assert.Contains("parameterized", guidance["rule"]!.GetValue<string>());
        Assert.Equal("Promoted", guidance["status"]!.GetValue<string>());
        Assert.NotNull(guidance["do_not"]);
        Assert.NotNull(guidance["applies_to"]);
        Assert.NotNull(guidance["confidence"]);
    }

    [Fact]
    public async Task SearchRulesTool_ExcludesSupersededAndArchived()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedRule(db, "concurrency", "Lock shared state.", RuleStatus.Archived);
        await SeedRule(db, "concurrency", "Lock shared state.", RuleStatus.Superseded);

        var tool = new Cli.Mcp.Tools.SearchRulesTool();
        await using var scope = db.CreateScope();

        var result = await tool.InvokeAsync(new JsonObject { ["query"] = "concurrency lock" },
            scope.ServiceProvider, CancellationToken.None);

        Assert.Equal(0, result["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task AddFeedbackTool_CreatesEventAndPendingRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var tool = new Cli.Mcp.Tools.AddFeedbackTool();

        JsonNode result;
        await using (var scope = db.CreateScope())
        {
            var args = new JsonObject
            {
                ["task"] = "write a SQL query",
                ["feedback"] = "use parameterized queries",
                ["bad_output"] = "string concatenation",
                ["tags"] = "sql,security",
            };
            result = await tool.InvokeAsync(args, scope.ServiceProvider, CancellationToken.None);
        }

        Assert.True(result["event_id"]!.GetValue<int>() > 0);
        Assert.True(result["rule_id"]!.GetValue<int>() > 0);
        Assert.Equal("Pending", result["status"]!.GetValue<string>());

        // Verify the data actually persisted.
        await using (var scope = db.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();
            var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();

            Assert.Single(await events.ListAsync());
            var allRules = await rules.ListAsync();
            Assert.Single(allRules);
            Assert.Equal(RuleStatus.Pending, allRules[0].Status);
        }
    }

    [Fact]
    public async Task GetProjectRulesTool_ReturnsScopedRules()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using (var scope = db.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            await repo.AddAsync(new RecallRule
            {
                Trigger = "t", RuleText = "Repo rule.", Mistake = "", TechnicalContext = "", Tags = "",
                Confidence = 0.6, Status = RuleStatus.Promoted, ScopeLevel = ScopeLevel.Repository, ScopeValue = "AgentRecall",
            });
            await repo.AddAsync(new RecallRule
            {
                Trigger = "t", RuleText = "Archived rule.", Mistake = "", TechnicalContext = "", Tags = "",
                Confidence = 0.6, Status = RuleStatus.Archived, ScopeLevel = ScopeLevel.Repository, ScopeValue = "AgentRecall",
            });
        }

        var tool = new Cli.Mcp.Tools.GetProjectRulesTool();
        await using var s = db.CreateScope();

        var args = new JsonObject { ["scope_level"] = "Repository", ["scope_value"] = "AgentRecall" };
        var result = await tool.InvokeAsync(args, s.ServiceProvider, CancellationToken.None);

        Assert.Equal(1, result["count"]!.GetValue<int>());
        Assert.Equal("Repo rule.", result["rules"]!.AsArray()[0]!["rule"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFoundError()
    {
        await using var db = new TestDatabase();
        var server = new McpServer(db.Services);

        var response = await server.HandleMessageAsync(
            """{"jsonrpc":"2.0","id":9,"method":"does/not/exist"}""", CancellationToken.None);

        Assert.Equal(-32601, response!["error"]!["code"]!.GetValue<int>());
    }
}
