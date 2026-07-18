using System.Text.Json.Nodes;
using AgentRecall.Cli;
using AgentRecall.Cli.Devcontainer;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture.Judge;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for the Turn Finalizer under the semantic capture judge: the model supplies a verdict
/// and AgentRecall validates + persists it. They drive <see cref="ITurnFinalizer"/> directly
/// (with the verdict supplied on the turn input) and the CLI command via stdin (with a
/// <c>judgment</c> object on the payload), exactly as the host does. No keyword heuristics
/// decide capture, and there is never a keyword fallback.
/// </summary>
[Collection("ConsoleStdin")]
public class TurnFinalizerTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static NormalizedRule Rule(
        string action = "consume or persist the payment token before claiming the card is saved",
        string condition = "when a validator requires a payment method token",
        string because = "a validate-and-drop flow creates false guarantees for later charging",
        string title = "Consume the payment token",
        string scope = "project",
        string? avoid = "validate-and-drop flows",
        string[]? tags = null) => new()
    {
        Title = title,
        Condition = condition,
        Action = action,
        Avoid = avoid,
        Because = because,
        Scope = scope,
        Tags = tags ?? [],
    };

    private static CaptureJudgeVerdict Verdict(
        JudgeDecision decision = JudgeDecision.Capture,
        double confidence = 0.9,
        JudgeCaptureReason reason = JudgeCaptureReason.ObservedAgentFailure,
        JudgeMemoryType memoryType = JudgeMemoryType.EngineeringLesson,
        NormalizedRule? rule = null,
        int? target = null,
        string? whyNotSaved = null,
        string? dedupeNotes = null) => new()
    {
        Decision = decision,
        Confidence = confidence,
        CaptureReason = reason,
        MemoryType = memoryType,
        NormalizedRule = rule ?? (decision is JudgeDecision.Skip or JudgeDecision.ReinforceExisting ? null : Rule()),
        TargetExistingRuleId = target,
        WhyNotSaved = whyNotSaved,
        DedupeNotes = dedupeNotes,
    };

    private static TurnFinalizationInput Turn(
        CaptureJudgeVerdict? judgment = null,
        string? prompt = null,
        string? assistant = null,
        bool? accepted = null,
        string? cwd = "/repo/project",
        string source = "stop_hook") =>
        new()
        {
            Prompt = prompt,
            AssistantResponse = assistant,
            Accepted = accepted,
            Source = source,
            Cwd = cwd,
            ScopeLevel = cwd is null ? ScopeLevel.Global : ScopeLevel.Repository,
            ScopeValue = cwd is null ? null : "project",
            SuppliedJudgment = judgment,
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

    // A. A high-confidence Capture verdict auto-captures an active rule.
    [Fact]
    public async Task CaptureVerdict_AutoCapturesActive()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(Verdict(confidence: 0.9)));

        var lesson = Assert.Single(result.Captured);
        Assert.Equal(RuleStatus.Active, (await Rules(db)).Single(r => r.Id == lesson.RuleId).Status);
        Assert.Empty(result.Suggested);
    }

    // C. A Skip verdict stores nothing.
    [Fact]
    public async Task SkipVerdict_ProducesNoRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(Verdict(
            decision: JudgeDecision.Skip, reason: JudgeCaptureReason.NotMemory, whyNotSaved: "no memory-worthy content")));

        Assert.Empty(result.Captured);
        Assert.Empty(result.Suggested);
        Assert.Empty(await Rules(db));
    }

    // D. A later turn repeating the same rule reinforces the existing one, not a duplicate.
    [Fact]
    public async Task DuplicateRule_ReinforcesExisting()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var first = await Finalize(db, Turn(Verdict(), prompt: "first turn"));
        var firstRuleId = Assert.Single(first.Captured).RuleId;

        // A distinct turn (distinct hash) with the same normalized rule.
        var second = await Finalize(db, Turn(Verdict(), prompt: "second turn"));

        Assert.Empty(second.Captured);
        Assert.Contains(firstRuleId, second.Duplicates);
        Assert.Single(await Rules(db));
    }

    // E. A manual capture earlier in the same turn prevents a duplicate judged capture.
    [Fact]
    public async Task ManualCaptureEarlierInTurn_PreventsDuplicate()
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

        // The judge captures the same guidance the manual path already stored.
        var result = await Finalize(db, Turn(Verdict(rule: Rule(action: manualText))));

        Assert.Empty(result.Captured);
        Assert.NotEmpty(result.Duplicates);
        Assert.Single(await Rules(db));
    }

    // F. A CodeFact verdict is skipped, not stored.
    [Fact]
    public async Task CodeFactVerdict_IsSkipped()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(Verdict(memoryType: JudgeMemoryType.CodeFact, confidence: 0.95)));

        Assert.Empty(result.Captured);
        Assert.NotEmpty(result.Skipped);
        Assert.Empty(await Rules(db));
    }

    // G. A repository-convention Capture verdict stores a repository rule.
    [Fact]
    public async Task RepositoryConventionVerdict_IsCaptured()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(Verdict(memoryType: JudgeMemoryType.RepositoryConvention)));

        var lesson = Assert.Single(result.Captured);
        Assert.Equal(RuleCategory.RepositoryConvention, lesson.Category);
        Assert.Single(await Rules(db));
    }

    // I. A mid-band confidence verdict is suggested (Pending), not auto-captured.
    [Fact]
    public async Task MidConfidenceVerdict_IsSuggestedNotAutoCaptured()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(Verdict(confidence: 0.65)));

        Assert.Empty(result.Captured);
        Assert.Single(result.Suggested);
        Assert.Equal(RuleStatus.Pending, (await Rules(db)).Single().Status);
    }

    // K. A SupersedeExisting verdict marks the old rule superseded and stores the replacement.
    [Fact]
    public async Task SupersedeVerdict_ReplacesExistingRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        int oldId;
        await using (var scope = db.CreateScope())
        {
            var feedback = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
            var seeded = await feedback.AddAsync(new FeedbackInput
            {
                Task = "work",
                Feedback = "Always use feature flags for new endpoints.",
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "project",
                AutoApprove = true,
            });
            oldId = seeded.Rule!.Id;
        }

        var result = await Finalize(db, Turn(Verdict(
            decision: JudgeDecision.SupersedeExisting, target: oldId,
            rule: Rule(action: "gate new endpoints behind IsEventsFeatureEnabled, not a raw feature flag"))));

        Assert.Single(result.Captured);
        await using var check = db.CreateScope();
        var rules = check.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        Assert.Equal(RuleStatus.Superseded, (await rules.GetAsync(oldId))!.Status);
    }

    // Judge input is bounded: a huge assistant response is truncated before the judge sees it.
    [Fact]
    public async Task HugeAssistantResponse_JudgeInputIsBounded()
    {
        var fake = new FakeCaptureJudge(Verdict());
        await using var db = new TestDatabase(
            o => o.MaxCandidateCharacters = 80,
            s => s.AddSingleton<ICaptureJudge>(fake));
        await Init(db);

        var huge = "We changed a lot of behaviour. " + new string('x', 5000);
        await Finalize(db, Turn(assistant: huge, prompt: "big turn"));

        Assert.NotNull(fake.LastInput);
        Assert.True((fake.LastInput!.AssistantSummary ?? string.Empty).Length <= 81);
    }

    // N. Malformed or empty payload (via the CLI) exits 0 and mutates nothing.
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

        // cwd is null, so the turn resolves to Global scope regardless of the rule's scope hint.
        var result = await Finalize(db, Turn(Verdict(), prompt: "x", cwd: null));

        var lesson = Assert.Single(result.Captured);
        Assert.Equal("Global", lesson.ScopeLabel);
    }

    // P. An explicit do-not-save verdict skips and records the reason.
    [Fact]
    public async Task DoNotSaveVerdict_Skips()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(Verdict(
            decision: JudgeDecision.Skip, reason: JudgeCaptureReason.ExplicitUserDoNotSave,
            whyNotSaved: "the user asked not to save this")));

        Assert.Empty(result.Captured);
        Assert.Empty(await Rules(db));
        Assert.Contains(result.Skipped, s => s.Reason.Contains("not to save", StringComparison.OrdinalIgnoreCase));
    }

    // Q. An explicit user-save verdict captures even at low confidence.
    [Fact]
    public async Task ExplicitSaveVerdict_CapturesActive()
    {
        await using var db = new TestDatabase(o => o.AutoApproveFeedback = false);
        await Init(db);

        var result = await Finalize(db, Turn(Verdict(
            reason: JudgeCaptureReason.ExplicitUserSave, confidence: 0.3)));

        Assert.Single(result.Captured);
        Assert.Equal(RuleStatus.Active, (await Rules(db)).Single().Status);
    }

    // T. Running the finalizer twice on the same turn is idempotent.
    [Fact]
    public async Task RunningTwice_IsIdempotent()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var input = Turn(Verdict(), prompt: "one turn");
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

        await Finalize(db, Turn(Verdict(), prompt: "one turn"));

        await using var scope = db.CreateScope();
        var finalizer = scope.ServiceProvider.GetRequiredService<ITurnFinalizer>();
        var last = await finalizer.GetLastAsync();

        Assert.NotNull(last);
        Assert.Single(last!.Captured);
        Assert.Equal(TurnFinalizer.JudgeDecisionSource, last.DecisionSource);
    }

    // U2. A model-supplied judgment for a turn still wins "last" even when the native Stop
    // hook fires afterward for the same turn with no judgment (recorded as "unavailable").
    [Fact]
    public async Task Status_PrefersJudgedDecisionOverLaterUnavailableForSameTurn()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // Same cwd + prompt => same turn correlation id; distinct source => distinct hash,
        // so this is not treated as an idempotent replay of the first finalization.
        await Finalize(db, Turn(Verdict(), prompt: "shared turn", source: "model-self-judged"));
        await Finalize(db, Turn(judgment: null, prompt: "shared turn", source: "stop_hook"));

        await using var scope = db.CreateScope();
        var finalizer = scope.ServiceProvider.GetRequiredService<ITurnFinalizer>();
        var last = await finalizer.GetLastAsync();

        Assert.NotNull(last);
        Assert.Equal(TurnFinalizer.JudgeDecisionSource, last!.DecisionSource);
        Assert.Single(last.Captured);
    }

    // V. JSON output is valid and carries the documented shape plus the judge decision fields.
    [Fact]
    public async Task JsonOutput_IsValidAndShaped()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var originalIn = Console.In;
        Console.SetIn(new StringReader(PayloadWithJudgment().ToJsonString()));
        try
        {
            var output = new StringWriter();
            var code = await CommandRouter.RunAsync(["finalize-turn", "--json"], db.Services, output);
            Assert.Equal(0, code);

            var node = JsonNode.Parse(output.ToString())!;
            Assert.NotNull(node["captured"]!.AsArray());
            Assert.NotNull(node["suggested"]!.AsArray());
            Assert.NotNull(node["skipped"]!.AsArray());
            Assert.Single(node["captured"]!.AsArray());
            Assert.Equal("SemanticCaptureJudge", node["decisionSource"]!.GetValue<string>());
            Assert.Equal("Capture", node["decision"]!.GetValue<string>());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    // The Stop-hook (--hook) path emits a non-blocking Turn Memory Summary systemMessage on capture.
    [Fact]
    public async Task HookFlag_EmitsSystemMessageOnCapture()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var originalIn = Console.In;
        Console.SetIn(new StringReader(PayloadWithJudgment().ToJsonString()));
        try
        {
            var output = new StringWriter();
            var code = await CommandRouter.RunAsync(["finalize-turn", "--hook"], db.Services, output);

            Assert.Equal(0, code);
            var node = JsonNode.Parse(output.ToString().Trim())!;
            var message = node["systemMessage"]!.GetValue<string>();
            Assert.Contains("🧠 **AgentRecall:**", message, StringComparison.Ordinal);
            Assert.Contains("captured 1", message, StringComparison.Ordinal);
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

        await Finalize(db, Turn(Verdict(), prompt: "one turn"));

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

    /// <summary>A Stop-hook payload carrying a Capture judgment, as the host would supply.</summary>
    internal static JsonObject PayloadWithJudgment() => new()
    {
        ["prompt"] = "one turn",
        ["cwd"] = "/repo/project",
        ["source"] = "stop_hook",
        ["judgment"] = new JsonObject
        {
            ["decision"] = "Capture",
            ["memory_type"] = "EngineeringLesson",
            ["confidence"] = 0.9,
            ["capture_reason"] = "ObservedAgentFailure",
            ["normalized_rule"] = new JsonObject
            {
                ["title"] = "Consume the payment token",
                ["condition"] = "when a validator requires a payment method token",
                ["action"] = "consume or persist the payment token before claiming the card is saved",
                ["avoid"] = "validate-and-drop flows",
                ["because"] = "a validate-and-drop flow creates false guarantees for later charging",
                ["scope"] = "project",
                ["tags"] = new JsonArray { "payments" },
            },
        },
    };
}
