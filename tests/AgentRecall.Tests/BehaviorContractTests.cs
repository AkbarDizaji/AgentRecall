using System.Text.Json.Nodes;
using AgentRecall.Cli;
using AgentRecall.Cli.Devcontainer;
using AgentRecall.Cli.Mcp;
using AgentRecall.Cli.Mcp.Tools;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Locks the integration contract between AgentRecall and Claude Code: the scaffolded
/// CLAUDE.md guidance, the Stop hook, the status command/MCP tool, and the wording of
/// finalization output. These do not test Claude — they test the artifacts AgentRecall
/// controls that make the agent check finalization status instead of guessing.
/// </summary>
public class BehaviorContractTests
{
    private static readonly string Guidance = DevcontainerScaffolder.ClaudeMdGuidance;

    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    // ----- 2 & 8. CLAUDE.md guidance contract: check status, don't guess -----

    [Fact]
    public void Guidance_TellsAgentToCheckFinalizationStatus()
    {
        Assert.Contains("agentrecall finalize-turn status", Guidance, StringComparison.Ordinal);
        Assert.Contains("agentrecall capture-status --last-turn", Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Guidance_RequiresCheckingBeforeAnsweringCaptureQuestions()
    {
        // The instruction must tie "asks whether AgentRecall captured…" to checking status.
        Assert.Contains("check the finalization status", Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never guess", Guidance, StringComparison.OrdinalIgnoreCase);
        // A manual tool call is explicitly NOT the source of truth.
        Assert.Contains("not the source of truth", Guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guidance_DocumentsAllFourAnswerPatterns()
    {
        Assert.Contains("AgentRecall captured rule #X", Guidance, StringComparison.Ordinal);
        Assert.Contains("AgentRecall suggested pending rule #Y", Guidance, StringComparison.Ordinal);
        Assert.Contains("AgentRecall skipped capture", Guidance, StringComparison.Ordinal);
        Assert.Contains("No finalized AgentRecall capture is recorded", Guidance, StringComparison.Ordinal);
    }

    // ----- 3. Forbidden phrases only appear inside the explicit "do not say" block -----

    [Theory]
    [InlineData("may have captured")]
    [InlineData("probably captured")]
    [InlineData("I think the hook")]
    [InlineData("Want me to save it?")]
    public void Guidance_DoesNotRecommendForbiddenPhrases(string phrase)
    {
        // Everything from the "never say them" marker onward is the do-not-say block;
        // forbidden phrases are allowed there as examples but nowhere else.
        var markerIndex = Guidance.IndexOf("never say them", StringComparison.Ordinal);
        Assert.True(markerIndex > 0, "Guidance must contain an explicit 'never say them' block.");

        var recommendedRegion = Guidance[..markerIndex];
        Assert.DoesNotContain(phrase, recommendedRegion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guidance_ForbidsTheStopHookSpeculationPhraseExplicitly()
    {
        Assert.Contains("never say them", Guidance, StringComparison.Ordinal);
        Assert.Contains("The Stop hook may have captured it.", Guidance, StringComparison.Ordinal);
    }

    // ----- 4. Stop hook contract -----

    [Fact]
    public void StopHook_RunsFinalizeTurnWithPathPrefix()
    {
        var command = DevcontainerScaffolder.FinalizeTurnHookCommand;

        Assert.Contains("finalize-turn --hook", command, StringComparison.Ordinal);
        Assert.StartsWith("PATH=$HOME/.dotnet/tools:$PATH ", command);
        Assert.Contains(".dotnet/tools", command, StringComparison.Ordinal);

        // It must not call the MCP capture tool or the legacy capture hook command.
        Assert.DoesNotContain("capture_feedback", command, StringComparison.Ordinal);
        Assert.DoesNotContain("hook capture", command, StringComparison.Ordinal);
    }

    [Fact]
    public void DevcontainerInit_WiresStopHookToFinalizeTurn()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentrecall-bc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            DevcontainerScaffolder.Init(root);
            var settingsPath = Path.Combine(root, DevcontainerScaffolder.ClaudeSettingsRelativePath);
            var node = JsonNode.Parse(File.ReadAllText(settingsPath))!;
            var stop = node["hooks"]!["Stop"]!.AsArray();

            Assert.Single(stop);
            var command = stop[0]!["hooks"]![0]!["command"]!.GetValue<string>();
            Assert.Equal(DevcontainerScaffolder.FinalizeTurnHookCommand, command);
            Assert.Contains("finalize-turn --hook", command, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ----- 5. Status command contract (both spellings) -----

    [Theory]
    [InlineData("finalize-turn status")]
    [InlineData("capture-status --last-turn")]
    public async Task StatusCommand_ReturnsLatestFinalization(string commandLine)
    {
        await using var db = new TestDatabase();
        await Init(db);
        await FinalizeTurn(db, "We do not mock DbContext directly.");

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(commandLine.Split(' '), db.Services, output);

        Assert.Equal(0, code);
        Assert.Contains("Captured:", output.ToString(), StringComparison.Ordinal);
    }

    // ----- 6. MCP tool contract -----

    [Fact]
    public void Mcp_ExposesACaptureStatusTool()
    {
        var names = McpServer.DefaultTools().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("capture_status", names);
    }

    [Fact]
    public async Task CaptureStatusTool_ReportsCapturedRuleIdsAndSourceAndTimestamp()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await FinalizeTurn(db, "We do not mock DbContext directly.");

        await using var scope = db.CreateScope();
        var result = await new CaptureStatusTool().InvokeAsync(null, scope.ServiceProvider, CancellationToken.None);

        Assert.True(result["found"]!.GetValue<bool>());
        Assert.NotEmpty(result["captured_rule_ids"]!.AsArray());
        Assert.NotNull(result["source"]);
        Assert.NotNull(result["created_at"]);
        Assert.Contains("AgentRecall captured rule #", result["summary"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureStatusTool_WhenNothingFinalized_ReportsNotFound()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var result = await new CaptureStatusTool().InvokeAsync(null, scope.ServiceProvider, CancellationToken.None);

        Assert.False(result["found"]!.GetValue<bool>());
        Assert.Equal(TurnFinalizationFormatter.NoFinalization, result["summary"]!.GetValue<string>());
    }

    // ----- 7. Status output golden tests -----

    [Fact]
    public void StatusText_Golden_ShowsCapturedSkippedSuggestedSections()
    {
        var result = SeededResult();
        var text = TurnFinalizationFormatter.RenderText(result);

        Assert.Contains("Captured:", text, StringComparison.Ordinal);
        Assert.Contains("- #18 Engineering lesson: When validators load entities, apply the same tenant scope.", text, StringComparison.Ordinal);
        Assert.Contains("Skipped:", text, StringComparison.Ordinal);
        Assert.Contains("Duplicate of rule #12.", text, StringComparison.Ordinal);
        Assert.Contains("Suggested:", text, StringComparison.Ordinal);
        Assert.Contains("#19 Pending rule:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusSummary_Golden_PrefersCapturedRule()
    {
        Assert.Equal(
            "AgentRecall captured rule #18: When validators load entities, apply the same tenant scope.",
            TurnFinalizationFormatter.SummaryLine(SeededResult()));
    }

    [Fact]
    public void StatusJson_IsValidAndDeterministic()
    {
        // The same seeded result renders to byte-identical JSON every time.
        var first = ToJson(SeededResult());
        var second = ToJson(SeededResult());
        Assert.Equal(first, second);

        var node = JsonNode.Parse(first)!;
        Assert.Equal(18, node["captured"]![0]!["rule_id"]!.GetValue<int>());
        Assert.Equal(12, node["skipped"]![0]!["duplicate_of_rule_id"]!.GetValue<int>());
        Assert.Equal(19, node["suggested"]![0]!["rule_id"]!.GetValue<int>());
    }

    // ----- 9. Idempotency: status is read-only and stable -----

    [Fact]
    public async Task Status_IsReadOnly_AndStable()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await FinalizeTurn(db, "We do not mock DbContext directly.");

        var rulesBefore = (await Rules(db)).Count;

        string Run()
        {
            var output = new StringWriter();
            CommandRouter.RunAsync(["finalize-turn", "status"], db.Services, output).GetAwaiter().GetResult();
            return output.ToString();
        }

        var a = Run();
        var b = Run();
        var c = Run();

        Assert.Equal(a, b);
        Assert.Equal(b, c);
        Assert.Equal(rulesBefore, (await Rules(db)).Count);
    }

    // ----- 10. Duplicate coordination with a manual capture -----

    [Fact]
    public async Task ManualCaptureThenFinalize_StatusReportsDuplicateNotNewRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using (var scope = db.CreateScope())
        {
            var feedback = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
            await feedback.AddAsync(new FeedbackInput
            {
                Task = "work",
                Feedback = "We do not mock DbContext directly.",
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "project",
            });
        }

        var result = await FinalizeTurnResult(db, "We do not mock DbContext directly.");

        Assert.Empty(result.Captured);
        Assert.NotEmpty(result.Duplicates);
        Assert.Single(await Rules(db));

        await using var read = db.CreateScope();
        var status = await new CaptureStatusTool().InvokeAsync(null, read.ServiceProvider, CancellationToken.None);
        Assert.Contains("skipped capture: duplicate", status["summary"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    // ----- helpers -----

    private static TurnFinalizationResult SeededResult() => new()
    {
        Captured =
        [
            new FinalizedLesson
            {
                RuleId = 18,
                Category = RuleCategory.EngineeringLesson,
                Text = "When validators load entities, apply the same tenant scope.",
                ScopeLabel = "Repository:project",
                Confidence = 0.7,
            },
        ],
        Suggested =
        [
            new FinalizedLesson
            {
                RuleId = 19,
                Category = RuleCategory.RepositoryConvention,
                Text = "Prefer the canonical gate helper.",
                ScopeLabel = "Repository:project",
                Confidence = 0.55,
            },
        ],
        Skipped = [new SkippedLesson { Reason = "Duplicate of rule #12.", DuplicateOfRuleId = 12 }],
        Duplicates = [12],
        Source = "stop_hook",
    };

    private static string ToJson(TurnFinalizationResult r) =>
        new JsonObject
        {
            ["captured"] = new JsonArray(r.Captured.Select(l => (JsonNode)new JsonObject
            {
                ["rule_id"] = l.RuleId,
                ["category"] = l.Category.ToString(),
                ["text"] = l.Text,
            }).ToArray()),
            ["suggested"] = new JsonArray(r.Suggested.Select(l => (JsonNode)new JsonObject
            {
                ["rule_id"] = l.RuleId,
                ["text"] = l.Text,
            }).ToArray()),
            ["skipped"] = new JsonArray(r.Skipped.Select(s => (JsonNode)new JsonObject
            {
                ["reason"] = s.Reason,
                ["duplicate_of_rule_id"] = s.DuplicateOfRuleId,
            }).ToArray()),
            ["duplicates"] = new JsonArray(r.Duplicates.Select(i => (JsonNode)i).ToArray()),
        }.ToJsonString();

    private static async Task FinalizeTurn(TestDatabase db, string prompt) =>
        await FinalizeTurnResult(db, prompt);

    private static async Task<TurnFinalizationResult> FinalizeTurnResult(TestDatabase db, string prompt)
    {
        await using var scope = db.CreateScope();
        var finalizer = scope.ServiceProvider.GetRequiredService<ITurnFinalizer>();
        return await finalizer.FinalizeAsync(new TurnFinalizationInput
        {
            Prompt = prompt,
            Source = "stop_hook",
            Cwd = "/repo/project",
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "project",
        });
    }

    private static async Task<IReadOnlyList<RecallRule>> Rules(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync();
    }
}
