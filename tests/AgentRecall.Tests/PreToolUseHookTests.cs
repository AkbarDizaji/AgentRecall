using AgentRecall.Cli.Hooks;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Covers the PreToolUse hook: recall keyed on the file about to be written, so a rule
/// surfaces at the moment the matching artifact is created — even when the turn's opening
/// prompt gave no signal it was coming.
/// </summary>
public class PreToolUseHookTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    /// <summary>A convention that only becomes relevant once a service class is being written.</summary>
    private static async Task SeedServiceRule(TestDatabase db, RuleStatus status = RuleStatus.Promoted)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        await repo.AddAsync(new RecallRule
        {
            Trigger = "writing a service class",
            RuleText = "In services, make methods public virtual instead of private so tests can override them.",
            Mistake = "Avoid private methods in services; they can't be black-boxed in tests.",
            TechnicalContext = "", Tags = "service,services,testing,virtual",
            Confidence = 0.9, Status = status, ScopeLevel = ScopeLevel.Global, ScopeValue = "",
        });
    }

    private static string WritePayload(string filePath, string content) =>
        "{\"tool_name\": \"Write\", \"cwd\": \"/tmp/project\", \"tool_input\": {\"file_path\": "
        + Json(filePath) + ", \"content\": " + Json(content) + "}}";

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    // ---- Happy path -----------------------------------------------------------

    [Fact]
    public async Task Write_ToMatchingFile_InjectsRelevantRule()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedServiceRule(db);

        var output = await PreToolUseHook.RunAsync(
            WritePayload("/tmp/project/OrderService.cs", "public class OrderService { private void Charge() {} }"),
            db.Services, new StringWriter());

        Assert.Contains("## AgentRecall Technical Context", output.AdditionalContext);
        Assert.Contains("public virtual", output.AdditionalContext);
        Assert.Contains("Source Rules:", output.AdditionalContext);
    }

    [Fact]
    public async Task Write_SurfacesFileScopedRule_BoundToThatFile()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // A rule scoped to the file being written, stored with a repository-relative path.
        await using (var scope = db.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            await repo.AddAsync(new RecallRule
            {
                Trigger = "editing the order service",
                RuleText = "In OrderService, keep methods public virtual for testability.",
                Mistake = "", TechnicalContext = "", Tags = "order",
                Confidence = 0.9, Status = RuleStatus.Promoted,
                ScopeLevel = ScopeLevel.File, ScopeValue = "OrderService.cs",
            });
        }

        var output = await PreToolUseHook.RunAsync(
            WritePayload("/tmp/project/OrderService.cs", "public class OrderService { }"),
            db.Services, new StringWriter());

        Assert.Contains("## AgentRecall Technical Context", output.AdditionalContext);
        Assert.Contains("public virtual", output.AdditionalContext);
    }

    [Fact]
    public async Task Write_ToUnrelatedFile_InjectsNothing()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedServiceRule(db);

        // A README write shares no terms with the services rule, so recall returns nothing.
        var output = await PreToolUseHook.RunAsync(
            WritePayload("/tmp/project/README.md", "# Project\nInstall instructions."),
            db.Services, new StringWriter());

        Assert.True(output.IsEmpty);
    }

    // ---- Turn correlation -----------------------------------------------------

    [Fact]
    public async Task RecordedTurnId_MatchesPromptDerivedId_SoRetrievalJoinsTheTurn()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedServiceRule(db);

        // The hook derives the turn id from the turn's prompt (read from the transcript),
        // exactly as the UserPromptSubmit and Stop hooks do — not from the file path.
        const string prompt = "implement the order service";
        var transcriptLine = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":" + Json(prompt) + "}}";
        var payload =
            "{\"tool_name\": \"Write\", \"cwd\": \"/tmp/project\", "
            + "\"transcript\": " + Json(transcriptLine) + ", "
            + "\"tool_input\": {\"file_path\": \"/tmp/project/OrderService.cs\", "
            + "\"content\": \"public class OrderService {}\"}}";

        var output = await PreToolUseHook.RunAsync(payload, db.Services, new StringWriter());
        Assert.False(output.IsEmpty);

        await using var scope = db.CreateScope();
        var latest = await scope.ServiceProvider
            .GetRequiredService<IAgentRecallActivityRepository>().GetLatestAsync();

        var expected = TurnCorrelation.Compute("/tmp/project", prompt);
        Assert.False(string.IsNullOrEmpty(expected));
        Assert.Equal(expected, latest!.TurnId);
    }

    [Fact]
    public async Task RecordedTurnId_IsNull_WhenNoTurnPromptIsAvailable()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedServiceRule(db);

        // No transcript/prompt on the payload: rather than a bogus id, TurnId is left null so
        // the summary's time-window fallback (which requires an empty TurnId) can still pick it up.
        var output = await PreToolUseHook.RunAsync(
            WritePayload("/tmp/project/OrderService.cs", "public class OrderService {}"),
            db.Services, new StringWriter());
        Assert.False(output.IsEmpty);

        await using var scope = db.CreateScope();
        var latest = await scope.ServiceProvider
            .GetRequiredService<IAgentRecallActivityRepository>().GetLatestAsync();

        Assert.True(string.IsNullOrEmpty(latest!.TurnId));
    }

    // ---- Notice channel -------------------------------------------------------

    [Fact]
    public async Task Injection_PutsRuleTextInContext_AndStatusNoticeInSystemMessage()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedServiceRule(db);

        var output = await PreToolUseHook.RunAsync(
            WritePayload("/tmp/project/OrderService.cs", "public class OrderService { private void X() {} }"),
            db.Services, new StringWriter());

        Assert.False(output.IsEmpty);
        // The model-facing context is the rule block only — no "fetched N rules" status chatter.
        Assert.StartsWith("## AgentRecall Technical Context", output.AdditionalContext);
        Assert.DoesNotContain("fetched", output.AdditionalContext, StringComparison.OrdinalIgnoreCase);
        // The status line is delivered on the user-facing channel instead.
        Assert.False(string.IsNullOrEmpty(output.SystemMessage));
        Assert.Contains("fetched", output.SystemMessage!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Per-turn dedup (context bloat + usage inflation) ---------------------

    [Fact]
    public async Task SameTurn_SecondWrite_DedupsRule_AndRecordsUsageOnlyOnce()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // A rule matched by an explicit keyword both writes carry, so matching never depends on
        // camel-case tokenisation.
        int ruleId;
        await using (var scope = db.CreateScope())
        {
            var rule = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().AddAsync(new RecallRule
            {
                Trigger = "", RuleText = "Guard refund paths with an idempotency key.",
                Mistake = "", TechnicalContext = "", Tags = "idempotency",
                Confidence = 0.9, Status = RuleStatus.Promoted, ScopeLevel = ScopeLevel.Global, ScopeValue = "",
            });
            ruleId = rule.Id;
        }

        const string prompt = "add refund handling";
        string PayloadFor(string file)
        {
            var line = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":" + Json(prompt) + "}}";
            return "{\"tool_name\": \"Write\", \"cwd\": \"/tmp/project\", \"transcript\": " + Json(line)
                + ", \"tool_input\": {\"file_path\": " + Json(file)
                + ", \"content\": \"// idempotency\\npublic class X {}\"}}";
        }

        // Two writes in the SAME turn (same prompt → same turn id), each matching the rule.
        var first = await PreToolUseHook.RunAsync(PayloadFor("/tmp/project/A.cs"), db.Services, new StringWriter());
        var second = await PreToolUseHook.RunAsync(PayloadFor("/tmp/project/B.cs"), db.Services, new StringWriter());

        Assert.Contains("idempotency key", first.AdditionalContext);
        // The rule was already surfaced this turn, so the second write injects nothing.
        Assert.True(second.IsEmpty);

        // And usage is recorded exactly once for the turn, not once per write.
        await using var readScope = db.CreateScope();
        var events = await readScope.ServiceProvider.GetRequiredService<IRecallEventRepository>().ListAsync();
        var applied = events.Count(e => e.RuleId == ruleId && e.Type == RecallEventType.RuleApplied);
        Assert.Equal(1, applied);
    }

    [Fact]
    public async Task DifferentTurns_ReRecordUsage_BecauseDedupIsScopedToOneTurn()
    {
        await using var db = new TestDatabase();
        await Init(db);

        int ruleId;
        await using (var scope = db.CreateScope())
        {
            var rule = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().AddAsync(new RecallRule
            {
                Trigger = "", RuleText = "Guard refund paths with an idempotency key.",
                Mistake = "", TechnicalContext = "", Tags = "idempotency",
                Confidence = 0.9, Status = RuleStatus.Promoted, ScopeLevel = ScopeLevel.Global, ScopeValue = "",
            });
            ruleId = rule.Id;
        }

        string PayloadFor(string prompt)
        {
            var line = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":" + Json(prompt) + "}}";
            return "{\"tool_name\": \"Write\", \"cwd\": \"/tmp/project\", \"transcript\": " + Json(line)
                + ", \"tool_input\": {\"file_path\": \"/tmp/project/A.cs\""
                + ", \"content\": \"// idempotency\\npublic class X {}\"}}";
        }

        var first = await PreToolUseHook.RunAsync(PayloadFor("add refund handling"), db.Services, new StringWriter());
        var second = await PreToolUseHook.RunAsync(PayloadFor("now add a chargeback path"), db.Services, new StringWriter());

        Assert.False(first.IsEmpty);
        // A new turn (new prompt) surfaces the rule again — dedup does not leak across turns.
        Assert.False(second.IsEmpty);

        await using var readScope = db.CreateScope();
        var events = await readScope.ServiceProvider.GetRequiredService<IRecallEventRepository>().ListAsync();
        var applied = events.Count(e => e.RuleId == ruleId && e.Type == RecallEventType.RuleApplied);
        Assert.Equal(2, applied);
    }

    // ---- Tool / payload gating ------------------------------------------------

    [Fact]
    public async Task NonMutatingTool_InjectsNothing()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedServiceRule(db);

        var payload = """{"tool_name": "Read", "cwd": "/tmp/project", "tool_input": {"file_path": "/tmp/project/OrderService.cs"}}""";
        var output = await PreToolUseHook.RunAsync(payload, db.Services, new StringWriter());

        Assert.True(output.IsEmpty);
    }

    [Fact]
    public async Task MissingFilePath_InjectsNothing()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedServiceRule(db);

        var payload = """{"tool_name": "Write", "cwd": "/tmp/project", "tool_input": {"content": "public class OrderService {}"}}""";
        var output = await PreToolUseHook.RunAsync(payload, db.Services, new StringWriter());

        Assert.True(output.IsEmpty);
    }

    [Fact]
    public async Task MalformedPayload_DoesNotThrow_AndInjectsNothing()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var output = await PreToolUseHook.RunAsync("{ not json", db.Services, new StringWriter());

        Assert.True(output.IsEmpty);
    }

    [Fact]
    public async Task Disabled_InjectsNothing()
    {
        await using var db = new TestDatabase(o => o.HookEnabled = false);
        await Init(db);
        await SeedServiceRule(db);

        var output = await PreToolUseHook.RunAsync(
            WritePayload("/tmp/project/OrderService.cs", "public class OrderService {}"),
            db.Services, new StringWriter());

        Assert.True(output.IsEmpty);
    }

    // ---- Payload-shape tolerance ---------------------------------------------
    // The running Claude Code build and the documented hook payload disagree on some
    // field names; extraction must recall from the new code regardless of which is sent.

    [Fact]
    public async Task Edit_WithNewString_RecallsFromReplacementCode()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedServiceRule(db);

        var payload =
            "{\"tool_name\": \"Edit\", \"cwd\": \"/tmp/project\", \"tool_input\": {"
            + "\"file_path\": \"/tmp/project/OrderService.cs\", "
            + "\"old_string\": \"class OrderService {}\", "
            + "\"new_string\": \"class OrderService { private void Charge() {} }\"}}";
        var output = await PreToolUseHook.RunAsync(payload, db.Services, new StringWriter());

        Assert.Contains("public virtual", output.AdditionalContext);
    }

    [Fact]
    public async Task MultiEdit_JoinsEditStrings_AndReadsNestedFilePath()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedServiceRule(db);

        // No top-level file_path; it lives on the first edit — and edits use new_text.
        var payload = """
            {"tool_name": "MultiEdit", "cwd": "/tmp/project",
             "tool_input": {"edits": [
                {"file_path": "/tmp/project/OrderService.cs", "old_text": "a", "new_text": "public class OrderService"},
                {"old_text": "b", "new_text": "private void Charge() {}"}]}}
            """;
        var output = await PreToolUseHook.RunAsync(payload, db.Services, new StringWriter());

        Assert.Contains("## AgentRecall Technical Context", output.AdditionalContext);
        Assert.Contains("public virtual", output.AdditionalContext);
    }
}
