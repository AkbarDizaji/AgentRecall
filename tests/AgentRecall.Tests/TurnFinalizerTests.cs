using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecall.Cli;
using AgentRecall.Cli.Devcontainer;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for the Turn Finalizer: the canonical, deterministic capture path for a
/// completed turn. They drive <see cref="ITurnFinalizer"/> directly with a resolved
/// turn, and the CLI command via stdin, exactly as the Stop hook does.
/// </summary>
public class TurnFinalizerTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static TurnFinalizationInput Turn(
        string? prompt = null,
        string? assistant = null,
        bool? accepted = null,
        string? cwd = "/repo/project") =>
        new()
        {
            Prompt = prompt,
            AssistantResponse = assistant,
            Accepted = accepted,
            Source = "stop_hook",
            Cwd = cwd,
            ScopeLevel = cwd is null ? ScopeLevel.Global : ScopeLevel.Repository,
            ScopeValue = cwd is null ? null : "project",
        };

    private static async Task<TurnFinalizationResult> Finalize(TestDatabase db, TurnFinalizationInput input)
    {
        await using var scope = db.CreateScope();
        var finalizer = scope.ServiceProvider.GetRequiredService<ITurnFinalizer>();
        return await finalizer.FinalizeAsync(input);
    }

    private static async Task<IReadOnlyList<RecallRule>> Rules(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync();
    }

    // A. A strong self-identified lesson auto-captures without asking.
    [Fact]
    public async Task StrongSelfIdentifiedLesson_AutoCaptures()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            assistant: "One worth storing is: when validators load entities before controller execution, " +
                       "apply the same tenant scope before emitting entity-specific messages."));

        var lesson = Assert.Single(result.Captured);
        Assert.Equal(RuleStatus.Active, (await Rules(db)).Single(r => r.Id == lesson.RuleId).Status);
        Assert.Empty(result.Suggested);
    }

    // B. Agent asks "Want me to save it?" but the finalizer auto-captures when worthy.
    [Fact]
    public async Task AgentAsksToSave_FinalizerDecidesAndCaptures()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            assistant: "This is worth storing: when FluentValidation validators load entities, apply the same " +
                       "tenant scope and authorization before returning messages. Want me to save it?"));

        Assert.Single(result.Captured);
    }

    // C. No lesson in the turn produces no mutation.
    [Fact]
    public async Task NoLesson_ProducesNoMutation()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            prompt: "Add a new endpoint for users.",
            assistant: "Done. I added the endpoint and a test."));

        Assert.Empty(result.Captured);
        Assert.Empty(result.Suggested);
        Assert.Empty(await Rules(db));
    }

    // D. A duplicate of an already-captured rule is skipped, referencing the existing id.
    [Fact]
    public async Task DuplicateRule_IsSkippedReferencingExisting()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var first = await Finalize(db, Turn(prompt: "We do not mock DbContext directly.", assistant: "Okay."));
        var firstRuleId = Assert.Single(first.Captured).RuleId;

        // A later turn (distinct content, so a distinct hash) repeats the same lesson.
        var second = await Finalize(db, Turn(prompt: "We do not mock DbContext directly.", assistant: "Understood."));

        Assert.Empty(second.Captured);
        Assert.Contains(firstRuleId, second.Duplicates);
        Assert.Single(await Rules(db));
    }

    // E. A manual capture earlier in the same turn prevents a duplicate finalizer capture.
    [Fact]
    public async Task ManualCaptureEarlierInTurn_PreventsDuplicate()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // Simulate the agent having manually captured the lesson mid-turn.
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

        var result = await Finalize(db, Turn(prompt: "We do not mock DbContext directly."));

        Assert.Empty(result.Captured);
        Assert.NotEmpty(result.Duplicates);
        Assert.Single(await Rules(db));
    }

    // F. A code fact is skipped, not stored.
    [Fact]
    public async Task CodeFact_IsSkipped()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(prompt: "Use IsEventsFeatureEnabled."));

        Assert.Empty(result.Captured);
        Assert.NotEmpty(result.Skipped);
        Assert.Empty(await Rules(db));
    }

    // G. A repository convention is captured (or suggested) — never skipped.
    [Fact]
    public async Task RepositoryConvention_IsCapturedOrSuggested()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            prompt: "When implementing Events backend gates, use IsEventsFeatureEnabled instead of IsVenueMigratedFor."));

        Assert.Equal(1, result.Captured.Count + result.Suggested.Count);
        Assert.Single(await Rules(db));
    }

    // H. A security lesson auto-captures.
    [Fact]
    public async Task SecurityLesson_AutoCaptures()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            prompt: "When FluentValidation validators load entities before controller execution, apply the " +
                    "same tenant scope and authorization before emitting entity-specific messages."));

        Assert.Single(result.Captured);
    }

    // I. A generic textbook rule is suggested, not auto-captured.
    [Fact]
    public async Task GenericTextbookRule_IsSuggestedNotAutoCaptured()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(prompt: "Don't re-query what you already loaded."));

        Assert.Empty(result.Captured);
        Assert.Single(result.Suggested);
        Assert.Equal(RuleStatus.Pending, (await Rules(db)).Single().Status);
    }

    // J. A conditional performance rule is suggested or captured, never skipped.
    [Fact]
    public async Task ConditionalPerformanceRule_IsKept()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            prompt: "When a controller has already loaded, authorized, and tracked an entity in the same " +
                    "request, pass it to downstream logic instead of re-querying the same id, unless fresh " +
                    "data or independent authorization is required."));

        Assert.Equal(1, result.Captured.Count + result.Suggested.Count);
    }

    // K. A conflicting rule prevents auto-capture and is suggested with a conflict note.
    [Fact]
    public async Task ConflictingRule_IsSuggestedWithNote()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // Seed an active rule, then finalize the opposite guidance on the same subject.
        await using (var scope = db.CreateScope())
        {
            var feedback = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
            await feedback.AddAsync(new FeedbackInput
            {
                Task = "work",
                Feedback = "Always use feature flags for new endpoints.",
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "project",
                AutoApprove = true,
            });
        }

        var result = await Finalize(db, Turn(prompt: "Never use feature flags for new endpoints."));

        Assert.Empty(result.Captured);
        var suggested = Assert.Single(result.Suggested);
        Assert.Contains("Conflicts with rule", suggested.Note ?? string.Empty, StringComparison.Ordinal);
    }

    // L. Multiple candidates respect MaxCandidatesPerTurn and priority.
    [Fact]
    public async Task MultipleCandidates_RespectMaxAndPriority()
    {
        await using var db = new TestDatabase(o => o.MaxCandidatesPerTurn = 1);
        await Init(db);

        // A generic correction plus a security correction: only the highest-priority
        // (security) candidate survives the cap.
        var result = await Finalize(db, Turn(
            prompt: "Always add a comment. When emitting validator messages, apply the same tenant scope to " +
                    "avoid cross-tenant information disclosure."));

        var total = result.Captured.Count + result.Suggested.Count + result.Skipped.Count;
        Assert.Equal(1, total);
        // The surviving candidate is the security one (captured), not the generic comment rule.
        Assert.Single(result.Captured);
    }

    // M. A huge transcript does not crash and truncates the candidate safely.
    [Fact]
    public async Task HugeTranscript_TruncatesSafely()
    {
        await using var db = new TestDatabase(o => o.MaxCandidateCharacters = 80);
        await Init(db);

        var huge = "When emitting messages, always validate scope " + new string('x', 5000) + ".";
        var result = await Finalize(db, Turn(prompt: huge));

        var stored = await Rules(db);
        Assert.All(stored, r => Assert.True(r.RuleText.Length <= 200));
        Assert.Equal(1, result.Captured.Count + result.Suggested.Count);
    }

    // N. Malformed JSON (via the CLI) exits 0 and mutates nothing.
    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{}")]
    public async Task MalformedOrEmptyPayload_ExitsZeroNoMutation(string stdin)
    {
        await using var db = new TestDatabase();
        await Init(db);

        var originalIn = Console.In;
        Console.SetIn(new StringReader(stdin));
        try
        {
            var output = new StringWriter();
            var code = await CommandRouter.RunAsync(["finalize-turn"], db.Services, output);
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        Assert.Empty(await Rules(db));
    }

    // O. A missing cwd falls back safely (Global scope) without crashing.
    [Fact]
    public async Task MissingCwd_FallsBackToGlobal()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(prompt: "We do not mock DbContext directly.", cwd: null));

        var lesson = Assert.Single(result.Captured);
        Assert.Equal("Global", lesson.ScopeLabel);
    }

    // P. A "do not save" signal skips and persists the reason.
    [Fact]
    public async Task DoNotSaveSignal_Skips()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            prompt: "We do not mock DbContext directly, but do not save this."));

        Assert.Empty(result.Captured);
        Assert.Empty(await Rules(db));
        Assert.Contains(result.Skipped, s => s.Reason.Contains("do-not-save", StringComparison.OrdinalIgnoreCase));
    }

    // Q. An explicit "save this" signal captures a worthy lesson.
    [Fact]
    public async Task SaveThisSignal_CapturesWorthyLesson()
    {
        // Auto-approve off so only the acceptance signal can force the capture.
        await using var db = new TestDatabase(o => o.AutoApproveFeedback = false);
        await Init(db);

        var result = await Finalize(db, Turn(
            prompt: "We do not mock DbContext directly. Please save this."));

        Assert.Single(result.Captured);
        Assert.Equal(RuleStatus.Active, (await Rules(db)).Single().Status);
    }

    // R. An agent "not worth storing" signal is respected.
    [Fact]
    public async Task NotWorthStoringSignal_IsRespected()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            assistant: "When emitting messages, apply tenant scope. This is not worth storing though."));

        Assert.Empty(result.Captured);
        Assert.Empty(await Rules(db));
    }

    // S. Near-duplicates in the same turn create a single rule.
    [Fact]
    public async Task NearDuplicatesInSameTurn_CreateOneRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(
            prompt: "We do not mock DbContext directly. We do not mock DbContext directly."));

        Assert.Single(result.Captured);
        Assert.Single(await Rules(db));
    }

    // T. Running the finalizer twice is idempotent.
    [Fact]
    public async Task RunningTwice_IsIdempotent()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var input = Turn(prompt: "We do not mock DbContext directly.");
        var first = await Finalize(db, input);
        var second = await Finalize(db, input);

        Assert.Single(first.Captured);
        Assert.True(second.FromCache);
        Assert.Single(await Rules(db));
    }

    // U. The status command returns the last finalization result.
    [Fact]
    public async Task Status_ReturnsLastFinalization()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await Finalize(db, Turn(prompt: "We do not mock DbContext directly."));

        await using var scope = db.CreateScope();
        var finalizer = scope.ServiceProvider.GetRequiredService<ITurnFinalizer>();
        var last = await finalizer.GetLastAsync();

        Assert.NotNull(last);
        Assert.Single(last!.Captured);
    }

    // V. JSON output is valid and carries the documented shape.
    [Fact]
    public async Task JsonOutput_IsValidAndShaped()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var originalIn = Console.In;
        Console.SetIn(new StringReader(new JsonObject
        {
            ["prompt"] = "We do not mock DbContext directly.",
            ["cwd"] = "/repo/project",
            ["source"] = "stop_hook",
        }.ToJsonString()));
        try
        {
            var output = new StringWriter();
            var code = await CommandRouter.RunAsync(["finalize-turn", "--json"], db.Services, output);
            Assert.Equal(0, code);

            var node = JsonNode.Parse(output.ToString())!;
            Assert.NotNull(node["captured"]!.AsArray());
            Assert.NotNull(node["suggested"]!.AsArray());
            Assert.NotNull(node["skipped"]!.AsArray());
            Assert.NotNull(node["duplicates"]!.AsArray());
            Assert.NotNull(node["errors"]!.AsArray());
            Assert.Single(node["captured"]!.AsArray());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    // The Stop-hook (--hook) path emits a non-blocking systemMessage on capture.
    [Fact]
    public async Task HookFlag_EmitsSystemMessageOnCapture()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var originalIn = Console.In;
        Console.SetIn(new StringReader(new JsonObject
        {
            ["prompt"] = "We do not mock DbContext directly.",
            ["cwd"] = "/repo/project",
        }.ToJsonString()));
        try
        {
            var output = new StringWriter();
            var code = await CommandRouter.RunAsync(["finalize-turn", "--hook"], db.Services, output);

            Assert.Equal(0, code);
            var node = JsonNode.Parse(output.ToString().Trim())!;
            Assert.Contains("AgentRecall finalized", node["systemMessage"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    // The status command works through the CLI alias too.
    [Fact]
    public async Task CaptureStatusCommand_ReportsLastTurn()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await Finalize(db, Turn(prompt: "We do not mock DbContext directly."));

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["capture-status", "--last-turn"], db.Services, output);

        Assert.Equal(0, code);
        Assert.Contains("Captured:", output.ToString(), StringComparison.Ordinal);
    }

    // W. devcontainer init installs the Stop (finalize-turn) hook.
    [Fact]
    public void DevcontainerInit_InstallsFinalizeTurnHook()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentrecall-fin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            DevcontainerScaffolder.Init(root);
            var settingsPath = Path.Combine(root, DevcontainerScaffolder.ClaudeSettingsRelativePath);
            var node = JsonNode.Parse(File.ReadAllText(settingsPath))!;
            var command = node["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>();
            Assert.Equal(DevcontainerScaffolder.FinalizeTurnHookCommand, command);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // X. An existing legacy capture hook is upgraded in place (not duplicated).
    [Fact]
    public void DevcontainerInit_UpgradesLegacyCaptureHookInPlace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentrecall-up-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(root, DevcontainerScaffolder.ClaudeSettingsRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        try
        {
            // A project wired with the old capture command.
            File.WriteAllText(settingsPath, new JsonObject
            {
                ["hooks"] = new JsonObject
                {
                    ["Stop"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["hooks"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = "command",
                                    ["command"] = DevcontainerScaffolder.CaptureHookCommand,
                                },
                            },
                        },
                    },
                },
            }.ToJsonString());

            DevcontainerScaffolder.Init(root);

            var node = JsonNode.Parse(File.ReadAllText(settingsPath))!;
            var stop = node["hooks"]!["Stop"]!.AsArray();
            Assert.Single(stop);
            Assert.Equal(
                DevcontainerScaffolder.FinalizeTurnHookCommand,
                stop[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Y. Tests run against an isolated temp DB and never touch ~/.agentrecall.
    [Fact]
    public async Task TestDatabase_IsIsolatedFromUserHome()
    {
        await using var db = new TestDatabase();
        var home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentrecall");
        Assert.DoesNotContain(home, db.Options.DataDirectory, StringComparison.Ordinal);
    }
}
