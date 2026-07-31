using System.Text.Json.Nodes;
using AgentRecall.Cli;
using AgentRecall.Cli.Mcp.Tools;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// The capture-approval gate: every automatic Stop-hook capture is parked Pending by default
/// (see <c>TurnFinalizer.RequiresApprovalGate</c>), tagged with the host's <c>session_id</c> so
/// "yes to all" can resolve every rule pending in one chat. These tests exercise the
/// <c>resolve_pending_capture</c> MCP tool and the CLI's <c>--all-pending</c> form directly
/// against seeded Pending rules, plus the real session_id threading from a finalize-turn payload.
/// </summary>
[Collection("ConsoleStdin")]
public class PendingCaptureApprovalTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task<int> SeedPending(TestDatabase db, string ruleText, string sessionId)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var added = await repo.AddAsync(new RecallRule
        {
            Trigger = "trigger", RuleText = ruleText, Status = RuleStatus.Pending,
            Confidence = 0.9, ScopeLevel = ScopeLevel.Global, ScopeValue = "", SessionId = sessionId,
        });
        return added.Id;
    }

    private static async Task<RuleStatus> StatusOf(TestDatabase db, int ruleId)
    {
        await using var scope = db.CreateScope();
        var rule = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().GetAsync(ruleId);
        return rule!.Status;
    }

    // ---- resolve_pending_capture: single rule ---------------------------------

    [Fact]
    public async Task ResolveTool_ApproveSingleRule_PromotesToActive()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var ruleId = await SeedPending(db, "Do not mock DbContext directly.", "session-a");

        await using var scope = db.CreateScope();
        var result = await new ResolvePendingCaptureTool().InvokeAsync(
            new JsonObject { ["decision"] = "approve", ["rule_id"] = ruleId },
            scope.ServiceProvider, CancellationToken.None);

        Assert.True(result["resolved"]!.GetValue<bool>());
        Assert.Equal("Active", result["status"]!.GetValue<string>());
        Assert.Equal(RuleStatus.Active, await StatusOf(db, ruleId));
    }

    [Fact]
    public async Task ResolveTool_RejectSingleRule_Archives()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var ruleId = await SeedPending(db, "Do not mock DbContext directly.", "session-a");

        await using var scope = db.CreateScope();
        var result = await new ResolvePendingCaptureTool().InvokeAsync(
            new JsonObject { ["decision"] = "reject", ["rule_id"] = ruleId },
            scope.ServiceProvider, CancellationToken.None);

        Assert.True(result["resolved"]!.GetValue<bool>());
        Assert.Equal(RuleStatus.Archived, await StatusOf(db, ruleId));
    }

    [Fact]
    public async Task ResolveTool_ApproveMissingRuleId_ReturnsUnresolved()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var result = await new ResolvePendingCaptureTool().InvokeAsync(
            new JsonObject { ["decision"] = "approve" }, scope.ServiceProvider, CancellationToken.None);

        Assert.False(result["resolved"]!.GetValue<bool>());
        Assert.Contains("rule_id", result["reason"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveTool_UnknownDecision_ReturnsUnresolvedWithReason()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var result = await new ResolvePendingCaptureTool().InvokeAsync(
            new JsonObject { ["decision"] = "maybe" }, scope.ServiceProvider, CancellationToken.None);

        Assert.False(result["resolved"]!.GetValue<bool>());
        Assert.Contains("Unknown decision", result["reason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // ---- resolve_pending_capture: whole-session bulk actions -------------------

    [Fact]
    public async Task ResolveTool_ApproveAll_OnlyAffectsExplicitSession_LeavesOtherSessionPending()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var sessionA1 = await SeedPending(db, "Rule A1", "session-a");
        var sessionA2 = await SeedPending(db, "Rule A2", "session-a");
        var sessionB1 = await SeedPending(db, "Rule B1", "session-b");

        await using var scope = db.CreateScope();
        var result = await new ResolvePendingCaptureTool().InvokeAsync(
            new JsonObject { ["decision"] = "approve_all", ["session_id"] = "session-a" },
            scope.ServiceProvider, CancellationToken.None);

        Assert.True(result["resolved"]!.GetValue<bool>());
        Assert.Equal(2, result["count"]!.GetValue<int>());
        Assert.Equal("session-a", result["session_id"]!.GetValue<string>());

        Assert.Equal(RuleStatus.Active, await StatusOf(db, sessionA1));
        Assert.Equal(RuleStatus.Active, await StatusOf(db, sessionA2));
        // A different chat's still-pending rule is untouched by another chat's "yes to all".
        Assert.Equal(RuleStatus.Pending, await StatusOf(db, sessionB1));
    }

    [Fact]
    public async Task ResolveTool_ApproveAll_NoSessionGiven_DefaultsToMostRecentlyCapturedSession()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedPending(db, "Older rule", "session-old");
        var newer1 = await SeedPending(db, "Newer rule 1", "session-new");
        var newer2 = await SeedPending(db, "Newer rule 2", "session-new");

        await using var scope = db.CreateScope();
        var result = await new ResolvePendingCaptureTool().InvokeAsync(
            new JsonObject { ["decision"] = "approve_all" }, scope.ServiceProvider, CancellationToken.None);

        Assert.Equal("session-new", result["session_id"]!.GetValue<string>());
        Assert.Equal(RuleStatus.Active, await StatusOf(db, newer1));
        Assert.Equal(RuleStatus.Active, await StatusOf(db, newer2));
    }

    [Fact]
    public async Task ResolveTool_RejectAll_ArchivesEveryRuleInTheSession()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var r1 = await SeedPending(db, "Rule 1", "session-a");
        var r2 = await SeedPending(db, "Rule 2", "session-a");

        await using var scope = db.CreateScope();
        var result = await new ResolvePendingCaptureTool().InvokeAsync(
            new JsonObject { ["decision"] = "reject_all", ["session_id"] = "session-a" },
            scope.ServiceProvider, CancellationToken.None);

        Assert.Equal(2, result["count"]!.GetValue<int>());
        Assert.Equal(RuleStatus.Archived, await StatusOf(db, r1));
        Assert.Equal(RuleStatus.Archived, await StatusOf(db, r2));
    }

    [Fact]
    public async Task ResolveTool_ApproveAll_NothingPending_ReturnsUnresolvedWithReason()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var result = await new ResolvePendingCaptureTool().InvokeAsync(
            new JsonObject { ["decision"] = "approve_all" }, scope.ServiceProvider, CancellationToken.None);

        Assert.False(result["resolved"]!.GetValue<bool>());
        Assert.Equal(0, result["count"]!.GetValue<int>());
        Assert.Equal("Nothing is awaiting approval.", result["reason"]!.GetValue<string>());
    }

    // ---- Real session_id threading from a Stop-hook payload --------------------

    [Fact]
    public async Task FinalizeTurn_SessionIdFromPayload_IsStampedOnThePendingRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var payload = new JsonObject
        {
            ["prompt"] = "We got burned mocking DbContext directly.",
            ["cwd"] = "/repo/project",
            ["source"] = "stop_hook",
            ["session_id"] = "claude-session-xyz",
            ["judgment"] = new JsonObject
            {
                ["decision"] = "Capture",
                ["memory_type"] = "EngineeringLesson",
                ["confidence"] = 0.9,
                ["capture_reason"] = "ObservedAgentFailure",
                ["normalized_rule"] = new JsonObject
                {
                    ["title"] = "Do not mock DbContext directly",
                    ["condition"] = "when writing unit tests",
                    ["action"] = "use a real SQLite context",
                    ["because"] = "mocking hides bugs",
                    ["scope"] = "project",
                },
            },
        };

        var originalIn = Console.In;
        try
        {
            Console.SetIn(new StringReader(payload.ToJsonString()));
            var code = await CommandRouter.RunAsync(["finalize-turn"], db.Services, new StringWriter());
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        await using var scope = db.CreateScope();
        var rules = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync();
        var rule = Assert.Single(rules);
        Assert.Equal(RuleStatus.Pending, rule.Status);
        Assert.Equal("claude-session-xyz", rule.SessionId);
    }

    // ---- CLI bulk form ----------------------------------------------------------

    [Fact]
    public async Task Cli_RulesApproveAllPending_ApprovesEveryRuleInTheGivenSession()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var r1 = await SeedPending(db, "Rule 1", "session-a");
        var r2 = await SeedPending(db, "Rule 2", "session-a");
        var other = await SeedPending(db, "Rule other", "session-b");

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(
            ["rules", "approve", "--all-pending", "--session", "session-a"], db.Services, output);

        Assert.Equal(0, code);
        Assert.Contains("2 rule(s) approved", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(RuleStatus.Active, await StatusOf(db, r1));
        Assert.Equal(RuleStatus.Active, await StatusOf(db, r2));
        Assert.Equal(RuleStatus.Pending, await StatusOf(db, other));
    }

    [Fact]
    public async Task Cli_RulesArchiveAllPending_NothingPending_ReportsNothingToDo()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["rules", "archive", "--all-pending"], db.Services, output);

        Assert.Equal(0, code);
        Assert.Contains("Nothing is awaiting approval.", output.ToString(), StringComparison.Ordinal);
    }
}
