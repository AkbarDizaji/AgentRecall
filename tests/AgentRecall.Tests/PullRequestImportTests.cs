using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class PullRequestImportTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    // ---- Parser ---------------------------------------------------------------

    [Fact]
    public void Parse_GhApiArray_ExtractsBodiesAndMetadata()
    {
        var json = """
        [
          { "body": "Use parameterized queries here.", "user": { "login": "alice" }, "path": "Db.cs" },
          { "body": "LGTM" }
        ]
        """;

        var comments = PullRequestCommentParser.Parse(json);

        Assert.Equal(2, comments.Count);
        Assert.Equal("Use parameterized queries here.", comments[0].Body);
        Assert.Equal("alice", comments[0].Author);
        Assert.Equal("Db.cs", comments[0].Path);
    }

    [Fact]
    public void Parse_GhPrViewObject_ReadsCommentsArray()
    {
        var json = """
        { "title": "Add refunds", "comments": [ { "body": "Always validate currency codes." } ] }
        """;

        var comments = PullRequestCommentParser.Parse(json);

        var only = Assert.Single(comments);
        Assert.Equal("Always validate currency codes.", only.Body);
    }

    [Fact]
    public void Parse_PlainText_SplitsOnBlankLines()
    {
        var text = "Use a Money value object.\n\nDon't concatenate SQL strings.";

        var comments = PullRequestCommentParser.Parse(text);

        Assert.Equal(2, comments.Count);
        Assert.Equal("Use a Money value object.", comments[0].Body);
        Assert.Equal("Don't concatenate SQL strings.", comments[1].Body);
    }

    // ---- Service: filtering & capture -----------------------------------------

    [Fact]
    public async Task Import_OnlyActionableComments_BecomePendingRules()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var comments = new List<PullRequestComment>
        {
            new() { Body = "Use parameterized queries to avoid injection." }, // actionable
            new() { Body = "LGTM, nice work!" },                              // praise → skipped
            new() { Body = "Why is this method here?" },                      // question → skipped
            new() { Body = "Never swallow exceptions silently." },           // actionable
        };

        await using var scope = db.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<IPullRequestImportService>();

        var result = await importer.ImportAsync(comments, new PullRequestImportOptions());

        Assert.Equal(4, result.CommentsFound);
        Assert.Equal(2, result.RulesCreated);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(2, result.RuleIds.Count);

        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var all = await rules.ListAsync();
        Assert.Equal(2, all.Count);
        Assert.All(all, r => Assert.Equal(RuleStatus.Pending, r.Status));
        Assert.All(all, r => Assert.Contains("pr-review", r.Tags));
    }

    [Fact]
    public async Task Import_PreservesOriginalCommentAsEvent()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<IPullRequestImportService>();

        await importer.ImportAsync(
            [new PullRequestComment { Body = "Use parameterized queries to avoid injection." }],
            new PullRequestImportOptions { PullRequestTitle = "Add user lookup" });

        var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();
        var recorded = Assert.Single(await events.ListAsync());
        Assert.Equal("Add user lookup", recorded.Trigger);
        Assert.Contains("parameterized", recorded.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_AppliesScopeAndExtraTags()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<IPullRequestImportService>();

        await importer.ImportAsync(
            [new PullRequestComment { Body = "Always use the project SQL helper." }],
            new PullRequestImportOptions
            {
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "AgentRecall",
                Tags = "sql",
            });

        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var rule = Assert.Single(await rules.ListAsync());
        Assert.Equal(ScopeLevel.Repository, rule.ScopeLevel);
        Assert.Equal("AgentRecall", rule.ScopeValue);
        Assert.Contains("pr-review", rule.Tags);
        Assert.Contains("sql", rule.Tags);
    }

    [Fact]
    public async Task ImportFile_MissingFile_Throws()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<IPullRequestImportService>();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            importer.ImportFileAsync(Path.Combine(Path.GetTempPath(), "no-such-pr-xyz.json"), new PullRequestImportOptions()));
    }

    // ---- MCP tool -------------------------------------------------------------

    [Fact]
    public void Server_RegistersImportPrCommentsTool()
    {
        var names = AgentRecall.Cli.Mcp.McpServer.DefaultTools().Select(t => t.Name).ToHashSet();
        Assert.Contains("import_pr_comments", names);
    }

    [Fact]
    public async Task ImportPrCommentsTool_CapturesActionableComments()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var tool = new AgentRecall.Cli.Mcp.Tools.ImportPrCommentsTool();
        await using var scope = db.CreateScope();

        var args = new JsonObject
        {
            ["comments"] = new JsonArray
            {
                "Use parameterized queries to avoid injection.",
                "LGTM",
            },
            ["pr_title"] = "Add refund support",
        };
        var result = await tool.InvokeAsync(args, scope.ServiceProvider, CancellationToken.None);

        Assert.Equal(2, result["comments_found"]!.GetValue<int>());
        Assert.Equal(1, result["rules_created"]!.GetValue<int>());
        Assert.Equal(1, result["skipped"]!.GetValue<int>());
        Assert.Equal("Pending", result["status"]!.GetValue<string>());
        Assert.Single(result["rule_ids"]!.AsArray());
    }
}
