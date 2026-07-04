using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecall.Cli;
using AgentRecall.Cli.Devcontainer;
using AgentRecall.Cli.Hooks;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// End-to-end tests for the deterministic capture path (the Stop-hook entry point).
/// They drive <see cref="CaptureHook"/> directly with a hook payload, exactly as the
/// `agentrecall hook capture` command does.
/// </summary>
[Collection("ConsoleStdin")]
public class CaptureHookTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static string Payload(string prompt, bool? accepted = null)
    {
        var node = new JsonObject
        {
            ["prompt"] = prompt,
            ["cwd"] = "/tmp/project",
            ["hook_event_name"] = "Stop",
        };
        if (accepted is { } a)
        {
            node["accepted"] = a;
        }

        return node.ToJsonString();
    }

    private static async Task<IReadOnlyList<RecallRule>> Rules(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync();
    }

    // A. User correction → rule captured.
    [Fact]
    public async Task Capture_UserCorrection_CapturesRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var message = await CaptureHook.RunAsync(
            Payload("We do not mock DbContext directly."), db.Services, new StringWriter());

        Assert.NotNull(message);
        Assert.Contains("AgentRecall captured rule", message!, StringComparison.Ordinal);

        var rule = Assert.Single(await Rules(db));
        Assert.Contains("auto-capture", rule.Tags, StringComparison.Ordinal);
    }

    // B. Accepted review comment → Active rule.
    [Fact]
    public async Task Capture_AcceptedReviewComment_CreatesActiveRule()
    {
        // Default off so only acceptance can force Active — proving accepted=true drives it.
        await using var db = new TestDatabase(o => o.AutoApproveFeedback = false);
        await Init(db);

        var message = await CaptureHook.RunAsync(
            Payload("Always validate inputs at the API boundary.", accepted: true),
            db.Services, new StringWriter());

        Assert.NotNull(message);
        var rule = Assert.Single(await Rules(db));
        Assert.Equal(RuleStatus.Active, rule.Status);
    }

    // B2. Acceptance intent expressed in the prompt text (no `accepted` flag) forces Active.
    // The regex patterns tolerate intervening words that the old fixed phrases missed.
    [Theory]
    [InlineData("Always validate inputs at the API boundary. Apply the review comment.")]
    [InlineData("Always validate inputs at the API boundary. Please apply the reviewer's second comment.")]
    [InlineData("Always validate inputs at the API boundary. Do exactly what the reviewer said.")]
    [InlineData("Always validate inputs at the API boundary, per the review feedback.")]
    [InlineData("Always validate inputs at the API boundary, following the review suggestions.")]
    public async Task Capture_TextAcceptanceIntent_CreatesActiveRule(string prompt)
    {
        // Default off so only text-expressed acceptance can force Active.
        await using var db = new TestDatabase(o => o.AutoApproveFeedback = false);
        await Init(db);

        var message = await CaptureHook.RunAsync(Payload(prompt), db.Services, new StringWriter());

        Assert.NotNull(message);
        var rule = Assert.Single(await Rules(db));
        Assert.Equal(RuleStatus.Active, rule.Status);
    }

    // B3. A plain correction with no acceptance intent follows the default (Pending when off).
    [Fact]
    public async Task Capture_NoAcceptanceIntent_FollowsDefault_Pending()
    {
        await using var db = new TestDatabase(o => o.AutoApproveFeedback = false);
        await Init(db);

        var message = await CaptureHook.RunAsync(
            Payload("Always validate inputs at the API boundary."), db.Services, new StringWriter());

        Assert.NotNull(message);
        var rule = Assert.Single(await Rules(db));
        Assert.Equal(RuleStatus.Pending, rule.Status);
    }

    // C. Code fact → rejected (no rule).
    [Fact]
    public async Task Capture_CodeFact_IsRejected()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var message = await CaptureHook.RunAsync(
            Payload("Use IsEventsFeatureEnabled."), db.Services, new StringWriter());

        Assert.NotNull(message);
        Assert.Contains("AgentRecall skipped capture", message!, StringComparison.Ordinal);
        Assert.Empty(await Rules(db));
    }

    // D / L. Specific code fact → generalized feature-gate lesson.
    [Fact]
    public async Task Capture_SpecificFeatureGateFact_StoresGeneralizedLesson()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var message = await CaptureHook.RunAsync(
            Payload("Use IsEventsFeatureEnabled instead of IsVenueMigratedFor."),
            db.Services, new StringWriter());

        Assert.NotNull(message);
        Assert.Contains("AgentRecall captured generalized lesson", message!, StringComparison.Ordinal);

        var rule = Assert.Single(await Rules(db));
        Assert.Contains("feature gate", rule.RuleText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("frontend", rule.RuleText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("backend", rule.RuleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsEventsFeatureEnabled", rule.RuleText, StringComparison.Ordinal);
    }

    // E. No correction → no capture.
    [Fact]
    public async Task Capture_OrdinaryTaskPrompt_CapturesNothing()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var message = await CaptureHook.RunAsync(
            Payload("Add a new endpoint for users."), db.Services, new StringWriter());

        Assert.Null(message);
        Assert.Empty(await Rules(db));
    }

    // F. Duplicate lesson → no duplicate rule.
    [Fact]
    public async Task Capture_DuplicateLesson_DoesNotCreateDuplicate()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var first = await CaptureHook.RunAsync(
            Payload("We do not mock DbContext directly."), db.Services, new StringWriter());
        var second = await CaptureHook.RunAsync(
            Payload("We do not mock DbContext directly."), db.Services, new StringWriter());

        Assert.NotNull(first);
        // A reuse changes nothing visible, so the hook stays silent.
        Assert.Null(second);
        Assert.Single(await Rules(db));
    }

    // G. Hook executes safely on malformed input (never throws).
    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"transcript_path\": \"/no/such/file-xyz.jsonl\"}")]
    public async Task Capture_BadOrEmptyInput_ReturnsNullWithoutThrowing(string payload)
    {
        await using var db = new TestDatabase();
        await Init(db);

        var message = await CaptureHook.RunAsync(payload, db.Services, new StringWriter());

        Assert.Null(message);
        Assert.Empty(await Rules(db));
    }

    // Disabled → no-op even for a clear correction.
    [Fact]
    public async Task Capture_Disabled_CapturesNothing()
    {
        await using var db = new TestDatabase(o => o.CaptureHookEnabled = false);
        await Init(db);

        var message = await CaptureHook.RunAsync(
            Payload("We do not mock DbContext directly."), db.Services, new StringWriter());

        Assert.Null(message);
        Assert.Empty(await Rules(db));
    }

    // Transcript parsing: the correction comes from the JSONL transcript, not inline.
    [Fact]
    public async Task Capture_ReadsCorrectionFromTranscript()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var transcriptPath = Path.Combine(Path.GetTempPath(), $"agentrecall-transcript-{Guid.NewGuid():N}.jsonl");
        var lines = new[]
        {
            """{"type":"user","message":{"content":[{"type":"text","text":"add a service"}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Done."},{"type":"tool_use","name":"Edit","input":{}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"x","content":"ok"}]}}""",
            """{"type":"user","message":{"content":[{"type":"text","text":"We do not mock DbContext directly."}]}}""",
        };
        await File.WriteAllLinesAsync(transcriptPath, lines);

        try
        {
            var payload = new JsonObject
            {
                ["transcript_path"] = transcriptPath,
                ["cwd"] = "/tmp/project",
            }.ToJsonString();

            var message = await CaptureHook.RunAsync(payload, db.Services, new StringWriter());

            Assert.NotNull(message);
            Assert.Contains("AgentRecall captured rule", message!, StringComparison.Ordinal);
            var rule = Assert.Single(await Rules(db));
            Assert.Contains("DbContext", rule.RuleText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(transcriptPath);
        }
    }

    // H. The `hook capture` command always exits 0 (never blocks Claude Code).
    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"prompt\": \"Use IsEventsFeatureEnabled.\", \"cwd\": \"/tmp/project\"}")]
    public async Task HookCommand_AlwaysExitsZero(string stdin)
    {
        await using var db = new TestDatabase();
        await Init(db);

        var originalIn = Console.In;
        Console.SetIn(new StringReader(stdin));
        try
        {
            var output = new StringWriter();
            var code = await CommandRouter.RunAsync(["hook", "capture"], db.Services, output);
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public async Task HookCommand_EmitsSystemMessageJson_OnCapture()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var originalIn = Console.In;
        Console.SetIn(new StringReader(Payload("We do not mock DbContext directly.")));
        try
        {
            var output = new StringWriter();
            var code = await CommandRouter.RunAsync(["hook", "capture"], db.Services, output);

            Assert.Equal(0, code);
            var emitted = output.ToString().Trim();
            var node = JsonNode.Parse(emitted)!;
            Assert.Contains("AgentRecall captured", node["systemMessage"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    // I. devcontainer init installs the capture (Stop) hook, merge-safe and once.
    [Fact]
    public void DevcontainerInit_InstallsCaptureHook()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentrecall-cap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var result = DevcontainerScaffolder.Init(root);
            Assert.Equal(HookSetupOutcome.Merged, result.CaptureHookOutcome);

            var settingsPath = Path.Combine(root, DevcontainerScaffolder.ClaudeSettingsRelativePath);
            var node = JsonNode.Parse(File.ReadAllText(settingsPath))!;
            var stop = node["hooks"]!["Stop"]!.AsArray();
            var command = stop[0]!["hooks"]![0]!["command"]!.GetValue<string>();
            Assert.Equal(DevcontainerScaffolder.FinalizeTurnHookCommand, command);

            // Idempotent: re-running does not add a second Stop matcher.
            DevcontainerScaffolder.Init(root);
            var reparsed = JsonNode.Parse(File.ReadAllText(settingsPath))!;
            Assert.Single(reparsed["hooks"]!["Stop"]!.AsArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // J. Scaffolded CLAUDE.md teaches "Store lessons, not facts".
    [Fact]
    public void ClaudeMdGuidance_ContainsStoreLessonsNotFacts()
    {
        Assert.Contains("Store lessons, not facts", DevcontainerScaffolder.ClaudeMdGuidance, StringComparison.Ordinal);
    }
}
