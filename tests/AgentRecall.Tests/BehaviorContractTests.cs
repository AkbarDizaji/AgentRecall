using System.Text.Json.Nodes;
using AgentRecall.Cli;
using AgentRecall.Cli.Devcontainer;
using AgentRecall.Cli.Mcp;
using AgentRecall.Cli.Mcp.Tools;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture.Judge;
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

    // ----- 1. AgentRecall behavior contract: explicit decision table -----

    // A. The guidance carries a dedicated behavior-contract section.
    [Fact]
    public void Guidance_ContainsBehaviorContractSection() =>
        Assert.Contains("### AgentRecall behavior contract", Guidance, StringComparison.Ordinal);

    // B. It contains the decision table.
    [Fact]
    public void Guidance_ContainsDecisionTable()
    {
        Assert.Contains("| User asks | Agent must do |", Guidance, StringComparison.Ordinal);
        Assert.Contains("| --- | --- |", Guidance, StringComparison.Ordinal);
    }

    // C. The table maps each question class to the right command.
    [Theory]
    [InlineData("Did you save anything?", "agentrecall capture-status --last-turn")]
    [InlineData("Was anything captured?", "agentrecall capture-status --last-turn")]
    [InlineData("Any lesson for AgentRecall?", "agentrecall capture-status --last-turn")]
    [InlineData("Did AgentRecall run?", "agentrecall activity last")]
    [InlineData("What did AgentRecall do?", "agentrecall activity last")]
    [InlineData("What rules were fetched?", "agentrecall activity last")]
    [InlineData("Did the Stop hook capture anything?", "agentrecall finalize-turn status")]
    public void Guidance_DecisionTableMapsQuestionToCommand(string question, string command)
    {
        // The question and its command must sit on the same table row.
        var row = Guidance.Split('\n').FirstOrDefault(l => l.Contains(question, StringComparison.Ordinal));
        Assert.NotNull(row);
        Assert.Contains(command, row!, StringComparison.Ordinal);
    }

    // D. All four forbidden answers are listed — and only as forbidden, never as
    // recommended behaviour (i.e. they appear after the "never say them" marker).
    [Theory]
    [InlineData("I didn't manually call AgentRecall")]
    [InlineData("The Stop hook may have captured it.")]
    [InlineData("I don't control whether it fired")]
    [InlineData("Want me to save it?")]
    public void Guidance_ForbidsEachNonAnswer(string phrase)
    {
        Assert.Contains(phrase, Guidance, StringComparison.Ordinal);

        var markerIndex = Guidance.IndexOf("never say them", StringComparison.Ordinal);
        Assert.True(markerIndex > 0, "Guidance must contain a 'never say them' block.");
        Assert.DoesNotContain(phrase, Guidance[..markerIndex], StringComparison.OrdinalIgnoreCase);
    }

    // E. It instructs the agent to check status, report actual state, and not speculate
    // or answer purely from its own tool calls.
    [Fact]
    public void Guidance_InstructsCheckReportDoNotSpeculate()
    {
        Assert.Contains("do not guess", Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not answer based only on", Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Check AgentRecall status", Guidance, StringComparison.Ordinal);
        Assert.Contains("Report the actual recorded result", Guidance, StringComparison.Ordinal);
        // Manual capture is the last resort, gated on status + an explicit ask.
        Assert.Contains("Only offer manual capture", Guidance, StringComparison.Ordinal);
    }

    // The contract still points tool-using agents at the capture_status MCP tool.
    [Fact]
    public void Guidance_BehaviorContractReferencesCaptureStatusTool() =>
        Assert.Contains("capture_status", Guidance, StringComparison.Ordinal);

    // F. README documents the same status commands.
    [Fact]
    public void Readme_DocumentsStatusCommands()
    {
        var readme = File.ReadAllText(FindRepoFile("README.md"));
        Assert.Contains("agentrecall capture-status --last-turn", readme, StringComparison.Ordinal);
        Assert.Contains("agentrecall activity last", readme, StringComparison.Ordinal);
        Assert.Contains("agentrecall finalize-turn status", readme, StringComparison.Ordinal);
    }

    // G. devcontainer init refreshes an OLDER guidance block in place (no duplicate),
    // and is idempotent against the current block. H. isolated temp dirs.
    [Fact]
    public void EnsureGuidance_UpdatesOlderBlockInPlace_NoDuplicate()
    {
        var root = NewTempProject();
        try
        {
            var path = Path.Combine(root, DevcontainerScaffolder.ClaudeMdRelativePath);
            const string before = "# My Project\n\nProject notes that must survive.\n\n";
            const string after = "\n## My Own Section\n\nKeep this verbatim.\n";
            // A stale AgentRecall block from an older version, between the user's content.
            var stale = before + DevcontainerScaffolder.ClaudeMdHeading +
                "\n\nOutdated guidance with no behavior contract.\n" + after;
            File.WriteAllText(path, stale);

            var outcome = DevcontainerScaffolder.EnsureClaudeMdGuidance(root);
            Assert.Equal(GuidanceOutcome.Updated, outcome);

            var refreshed = File.ReadAllText(path);
            // Heading appears exactly once — the block was replaced, not duplicated.
            Assert.Equal(1, Occurrences(refreshed, DevcontainerScaffolder.ClaudeMdHeading));
            // The new behavior contract is now present; the stale text is gone.
            Assert.Contains("### AgentRecall behavior contract", refreshed, StringComparison.Ordinal);
            Assert.DoesNotContain("Outdated guidance with no behavior contract.", refreshed, StringComparison.Ordinal);
            // Surrounding user content is preserved on both sides.
            Assert.StartsWith(before, refreshed, StringComparison.Ordinal);
            Assert.Contains("## My Own Section", refreshed, StringComparison.Ordinal);
            Assert.Contains("Keep this verbatim.", refreshed, StringComparison.Ordinal);

            // Re-running is now a no-op (idempotent against the current block).
            Assert.Equal(GuidanceOutcome.AlreadyPresent, DevcontainerScaffolder.EnsureClaudeMdGuidance(root));
            Assert.Equal(refreshed, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTempProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentrecall-bc-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static int Occurrences(string haystack, string needle) =>
        haystack.Split(needle).Length - 1;

    private static string FindRepoFile(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate {fileName} above {AppContext.BaseDirectory}.");
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

    // The guidance must name the capture_status MCP tool so a tool-using agent calls it.
    [Fact]
    public void Guidance_InstructsCallingTheCaptureStatusTool()
    {
        Assert.Contains("capture_status", Guidance, StringComparison.Ordinal);
        // It must be presented as the thing to call, not buried.
        Assert.Contains("Call the `capture_status` MCP tool", Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Guidance_TellsAgentNotToAnswerFromMemory()
    {
        Assert.Contains("never from memory", Guidance, StringComparison.OrdinalIgnoreCase);
    }

    // The "I didn't manually call AgentRecall" non-answer is forbidden, and only inside
    // the do-not-say block — never as recommended behaviour.
    [Fact]
    public void Guidance_ForbidsTheManualCallNonAnswer()
    {
        var markerIndex = Guidance.IndexOf("never say them", StringComparison.Ordinal);
        Assert.True(markerIndex > 0);

        Assert.Contains("didn't manually call AgentRecall", Guidance, StringComparison.Ordinal);
        Assert.DoesNotContain("manually call AgentRecall", Guidance[..markerIndex], StringComparison.Ordinal);
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

    // ----- 4b. Self-reported judgment contract: the model is told to be the judge -----

    // The Stop hook fires with no judgment on its own payload, so the guidance must tell the
    // model to construct one itself and pipe it into finalize-turn on every substantive turn.
    [Fact]
    public void Guidance_InstructsSelfReportedJudgmentOnEverySubstantiveTurn()
    {
        Assert.Contains("You are the semantic judge", Guidance, StringComparison.Ordinal);
        Assert.Contains("echo '<payload>' | agentrecall finalize-turn", Guidance, StringComparison.Ordinal);
        Assert.Contains("substantive coding turn", Guidance, StringComparison.Ordinal);
    }

    // The documented JSON shape must match what TurnPayload.ParseJudgment actually reads.
    [Theory]
    [InlineData("\"cwd\"")]
    [InlineData("\"prompt\"")]
    [InlineData("\"assistant_response\"")]
    [InlineData("\"judgment\"")]
    [InlineData("\"decision\"")]
    [InlineData("\"memory_type\"")]
    [InlineData("\"confidence\"")]
    [InlineData("\"capture_reason\"")]
    [InlineData("\"target_existing_rule_id\"")]
    [InlineData("\"normalized_rule\"")]
    [InlineData("\"why_not_saved\"")]
    [InlineData("\"dedupe_notes\"")]
    public void Guidance_JudgmentPayloadNamesRealJsonKeys(string key) =>
        Assert.Contains(key, Guidance, StringComparison.Ordinal);

    // Every JudgeDecision/JudgeMemoryType/JudgeCaptureReason value named in the guidance must
    // actually parse as that enum, so the documented options never drift from the real ones.
    [Fact]
    public void Guidance_EnumOptionsListedForEachFieldAllParse()
    {
        var decisions = ExtractPipedOptions("\"decision\": \"");
        Assert.NotEmpty(decisions);
        Assert.All(decisions, d => Assert.True(Enum.TryParse<JudgeDecision>(d, out _), $"'{d}' is not a JudgeDecision"));

        var memoryTypes = ExtractPipedOptions("\"memory_type\": \"");
        Assert.NotEmpty(memoryTypes);
        Assert.All(memoryTypes, m => Assert.True(Enum.TryParse<JudgeMemoryType>(m, out _), $"'{m}' is not a JudgeMemoryType"));

        var reasons = ExtractPipedOptions("\"capture_reason\": \"");
        Assert.NotEmpty(reasons);
        Assert.All(reasons, r => Assert.True(Enum.TryParse<JudgeCaptureReason>(r, out _), $"'{r}' is not a JudgeCaptureReason"));
    }

    // The judge must reflect on friction that was never voiced as an explicit correction, not
    // only scan for explicit signals — and must route what it finds through the same
    // pending/review gate as any other ambiguous suggestion, never straight to Capture.
    [Fact]
    public void Guidance_InstructsReflectiveCheckBeforeDefaultingToSkip()
    {
        Assert.Contains("Before defaulting to Skip, reflect", Guidance, StringComparison.Ordinal);
        Assert.Contains("SelfIdentifiedFriction", Guidance, StringComparison.Ordinal);
        Assert.Contains("never `Capture`", Guidance, StringComparison.Ordinal);
    }

    // Required-field guidance per decision must be spelled out, not left implicit.
    [Theory]
    [InlineData("why_not_saved` is required")]
    [InlineData("target_existing_rule_id` and `dedupe_notes` are required")]
    public void Guidance_StatesRequiredFieldsPerDecision(string phrase) =>
        Assert.Contains(phrase, Guidance, StringComparison.Ordinal);

    private static List<string> ExtractPipedOptions(string fieldMarker)
    {
        var start = Guidance.IndexOf(fieldMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Guidance must document the {fieldMarker} field.");
        start += fieldMarker.Length;
        var end = Guidance.IndexOf('"', start);
        Assert.True(end > start, "Malformed documented field options.");
        return Guidance[start..end].Split('|', StringSplitOptions.TrimEntries).ToList();
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

    // The published tool list (over JSON-RPC, as Claude Code sees it) must include it.
    [Fact]
    public async Task Mcp_ToolsListOverJsonRpc_IncludesCaptureStatus()
    {
        await using var db = new TestDatabase();
        var server = new McpServer(db.Services);

        var response = await server.HandleMessageAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", CancellationToken.None);

        var names = response!["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("capture_status", names);
    }

    // Golden: a turn that captured exactly one rule is reported verbatim by capture_status.
    [Fact]
    public async Task CaptureStatusTool_Golden_ReturnsTheSeededCapturedRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        const string lesson = "When emitting validator messages, apply the same tenant scope.";
        var finalized = await FinalizeTurnResult(db, lesson);
        var expectedId = Assert.Single(finalized.Captured).RuleId;

        await using var scope = db.CreateScope();
        var result = await new CaptureStatusTool().InvokeAsync(null, scope.ServiceProvider, CancellationToken.None);

        Assert.True(result["found"]!.GetValue<bool>());
        var captured = result["captured"]!.AsArray();
        var only = Assert.Single(captured);
        Assert.Equal(expectedId, only!["rule_id"]!.GetValue<int>());
        Assert.Equal(lesson, only["text"]!.GetValue<string>());
        Assert.Equal(new[] { expectedId }, result["captured_rule_ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray());
        Assert.Contains($"AgentRecall captured rule #{expectedId}", result["summary"]!.GetValue<string>(), StringComparison.Ordinal);
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

        string manualText;
        await using (var scope = db.CreateScope())
        {
            var feedback = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
            var manual = await feedback.AddAsync(new FeedbackInput
            {
                Task = "work",
                Feedback = "We do not mock DbContext directly.",
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "project",
                AutoApprove = true,
            });
            manualText = manual.Rule!.RuleText;
        }

        // The judge captures the same guidance already stored → reinforced, not duplicated.
        var result = await FinalizeTurnResult(db, manualText);

        Assert.Empty(result.Captured);
        Assert.NotEmpty(result.Duplicates);
        Assert.Single(await Rules(db));

        await using var read = db.CreateScope();
        var status = await new CaptureStatusTool().InvokeAsync(null, read.ServiceProvider, CancellationToken.None);
        Assert.Contains("reinforced existing rule", status["summary"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
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
            // The host model judged the turn worthy; the rule text is the prompt verbatim so the
            // golden assertions can match it.
            SuppliedJudgment = JudgeVerdicts.Capture(rule: JudgeVerdicts.Rule(action: prompt)),
        });
    }

    private static async Task<IReadOnlyList<RecallRule>> Rules(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync();
    }
}
