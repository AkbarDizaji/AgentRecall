using AgentRecall.Cli.Hooks;
using AgentRecall.Core.Abstractions;
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

        Assert.Contains("## AgentRecall Technical Context", output);
        Assert.Contains("public virtual", output);
        Assert.Contains("Source Rules:", output);
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

        Assert.Equal(string.Empty, output);
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

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task MissingFilePath_InjectsNothing()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedServiceRule(db);

        var payload = """{"tool_name": "Write", "cwd": "/tmp/project", "tool_input": {"content": "public class OrderService {}"}}""";
        var output = await PreToolUseHook.RunAsync(payload, db.Services, new StringWriter());

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task MalformedPayload_DoesNotThrow_AndInjectsNothing()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var output = await PreToolUseHook.RunAsync("{ not json", db.Services, new StringWriter());

        Assert.Equal(string.Empty, output);
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

        Assert.Equal(string.Empty, output);
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

        Assert.Contains("public virtual", output);
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

        Assert.Contains("## AgentRecall Technical Context", output);
        Assert.Contains("public virtual", output);
    }
}
