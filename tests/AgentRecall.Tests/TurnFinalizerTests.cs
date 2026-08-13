using System.Text.Json.Nodes;
using AgentRecall.Cli;
using AgentRecall.Cli.Devcontainer;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture;
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

    // A. By default, even a high-confidence Capture verdict is parked pending the user's
    // yes/no/"yes to all" approval instead of stored Active outright.
    [Fact]
    public async Task CaptureVerdict_DefaultRequiresApproval_StoresPending()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(Verdict(confidence: 0.9)));

        var lesson = Assert.Single(result.Captured);
        Assert.True(lesson.AwaitingApproval);
        Assert.Equal(RuleStatus.Pending, (await Rules(db)).Single(r => r.Id == lesson.RuleId).Status);
        Assert.Empty(result.Suggested);
    }

    // A2. InteractiveMemoryMode=Silent is the global bypass: a high-confidence capture is
    // stored Active immediately, exactly as before the approval gate existed.
    [Fact]
    public async Task CaptureVerdict_SilentMode_BypassesApproval_StoresActive()
    {
        await using var db = new TestDatabase(o => o.InteractiveMemoryMode = "Silent");
        await Init(db);

        var result = await Finalize(db, Turn(Verdict(confidence: 0.9)));

        var lesson = Assert.Single(result.Captured);
        Assert.False(lesson.AwaitingApproval);
        Assert.Equal(RuleStatus.Active, (await Rules(db)).Single(r => r.Id == lesson.RuleId).Status);
    }

    // A3. An explicit user save always bypasses the gate too — the user already said yes.
    [Fact]
    public async Task CaptureVerdict_ExplicitUserSave_BypassesApproval_StoresActive()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(Verdict(
            confidence: 0.9, reason: JudgeCaptureReason.ExplicitUserSave)));

        var lesson = Assert.Single(result.Captured);
        Assert.False(lesson.AwaitingApproval);
        Assert.Equal(RuleStatus.Active, (await Rules(db)).Single(r => r.Id == lesson.RuleId).Status);
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

    [Fact]
    public async Task FinalizeAsync_NullInput_Throws()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var finalizer = scope.ServiceProvider.GetRequiredService<ITurnFinalizer>();

        await Assert.ThrowsAsync<ArgumentNullException>(() => finalizer.FinalizeAsync(null!));
    }

    // The finalizer is a no-op (not just "produces nothing") when turned off — no DB row at all.
    [Fact]
    public async Task TurnFinalizerDisabled_ReturnsEmptyResult_PersistsNothing()
    {
        await using var db = new TestDatabase(o => o.TurnFinalizerEnabled = false);
        await Init(db);

        var result = await Finalize(db, Turn(Verdict(), prompt: "one turn"));

        Assert.Empty(result.Captured);
        Assert.Empty(result.Suggested);
        Assert.Empty(result.Skipped);
        Assert.Null(result.Id);
        Assert.Empty(await Rules(db));
    }

    [Fact]
    public async Task CaptureJudgeModeOff_ReturnsEmptyResult_PersistsNothing()
    {
        await using var db = new TestDatabase(o => o.CaptureJudgeMode = nameof(CaptureJudgeMode.Off));
        await Init(db);

        var result = await Finalize(db, Turn(Verdict(), prompt: "one turn"));

        Assert.Empty(result.Captured);
        Assert.Null(result.Id);
        Assert.Empty(await Rules(db));
    }

    // ---- Reinforce path (JudgeDecision.ReinforceExisting) -----------------------

    [Fact]
    public async Task ReinforceExisting_RaisesConfidence_NoNewRuleCreated()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var first = await Finalize(db, Turn(Verdict(confidence: 0.9), prompt: "first turn"));
        var existingId = Assert.Single(first.Captured).RuleId;
        var before = (await Rules(db)).Single(r => r.Id == existingId).Confidence;

        var result = await Finalize(db, Turn(
            Verdict(decision: JudgeDecision.ReinforceExisting, target: existingId, dedupeNotes: "same guidance"),
            prompt: "second turn"));

        Assert.Empty(result.Captured);
        Assert.Contains(existingId, result.Duplicates);
        Assert.Single(await Rules(db));
        var after = (await Rules(db)).Single(r => r.Id == existingId).Confidence;
        Assert.Equal(Math.Round(Math.Min(1.0, before + 0.1), 2), after);
    }

    // A repeated correction against a not-yet-standing rule promotes it to always-apply, and
    // the skip message names the promotion specifically.
    [Fact]
    public async Task ReinforceExisting_RepeatedCorrection_PromotesToAlwaysApply()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var first = await Finalize(db, Turn(Verdict(confidence: 0.9), prompt: "first turn"));
        var existingId = Assert.Single(first.Captured).RuleId;
        Assert.False((await Rules(db)).Single(r => r.Id == existingId).AlwaysApply);

        var result = await Finalize(db, Turn(
            Verdict(decision: JudgeDecision.ReinforceExisting, reason: JudgeCaptureReason.RepeatedMistake,
                target: existingId, dedupeNotes: "same guidance"),
            prompt: "second turn"));

        Assert.True((await Rules(db)).Single(r => r.Id == existingId).AlwaysApply);
        Assert.Contains(result.Skipped, s =>
            s.Reason.Contains("promoted it to a standing rule", StringComparison.OrdinalIgnoreCase));
    }

    // Reinforcing a rule that is already always-apply does not re-announce a promotion.
    [Fact]
    public async Task ReinforceExisting_AlreadyAlwaysApply_DoesNotReannouncePromotion()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var first = await Finalize(db, Turn(
            Verdict(reason: JudgeCaptureReason.UserPreference, memoryType: JudgeMemoryType.UserPreference, confidence: 0.9),
            prompt: "first turn"));
        var existingId = Assert.Single(first.Captured).RuleId;
        Assert.True((await Rules(db)).Single(r => r.Id == existingId).AlwaysApply);

        var result = await Finalize(db, Turn(
            Verdict(decision: JudgeDecision.ReinforceExisting, reason: JudgeCaptureReason.RepeatedMistake,
                target: existingId, dedupeNotes: "same guidance"),
            prompt: "second turn"));

        Assert.DoesNotContain(result.Skipped, s =>
            s.Reason.Contains("promoted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Skipped, s => s.Reason == $"Reinforced existing rule #{existingId}.");
    }

    // A reinforce target that no longer exists (deleted between judging and persisting) is a
    // clean no-op, not a crash — and it does not silently add a bogus duplicate.
    [Fact]
    public async Task ReinforceExisting_TargetNoLongerExists_SkipsWithoutDuplicate()
    {
        await using var db = new TestDatabase();
        await Init(db);

        const int missingId = 999_999;
        var result = await Finalize(db, Turn(
            Verdict(decision: JudgeDecision.ReinforceExisting, target: missingId, dedupeNotes: "same guidance"),
            prompt: "one turn"));

        Assert.Empty(result.Duplicates);
        Assert.Contains(result.Skipped, s =>
            s.Reason.Contains("no longer exists", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Supersede edge cases ----------------------------------------------------

    // Superseding with a target that no longer exists must not attempt to mark it superseded
    // (that would throw, since the lifecycle service requires the target to exist) — it still
    // stores the new rule cleanly, with no error recorded.
    [Fact]
    public async Task Supersede_TargetNoLongerExists_StillCapturesCleanly_NoError()
    {
        await using var db = new TestDatabase();
        await Init(db);

        const int missingId = 999_999;
        var result = await Finalize(db, Turn(Verdict(
            decision: JudgeDecision.SupersedeExisting, target: missingId, rule: Rule())));

        Assert.Empty(result.Errors);
        Assert.Single(result.Captured);
    }

    // The rule the judge wants to supersede with turns out to be a duplicate of an existing
    // rule: dedupe wins, nothing new is captured or superseded.
    [Fact]
    public async Task Supersede_NewRuleIsDuplicate_ReinforcesInsteadOfSuperseding()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var first = await Finalize(db, Turn(Verdict(confidence: 0.9), prompt: "first turn"));
        var existingId = Assert.Single(first.Captured).RuleId;

        var result = await Finalize(db, Turn(Verdict(
            decision: JudgeDecision.SupersedeExisting, target: existingId, rule: Rule()), prompt: "second turn"));

        Assert.Empty(result.Captured);
        Assert.Contains(existingId, result.Duplicates);
        Assert.Single(await Rules(db));
    }

    // ---- AlwaysApply / scope for preference categories --------------------------

    // A user-preference verdict is stored at Global scope even without the judge's
    // always_apply flag set, because preferences are standing by category, not by flag.
    [Fact]
    public async Task UserPreferenceVerdict_IsGlobalScoped_EvenWithoutAlwaysApplyFlag()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(Verdict(
            reason: JudgeCaptureReason.UserPreference, memoryType: JudgeMemoryType.UserPreference, confidence: 0.9),
            cwd: "/repo/project"));

        var lesson = Assert.Single(result.Captured);
        Assert.Equal("Global", lesson.ScopeLabel);
        Assert.True(lesson.AlwaysApply);
    }

    // An ordinary engineering lesson (not a preference, not flagged always-apply) keeps the
    // turn's repository scope rather than being forced Global.
    [Fact]
    public async Task OrdinaryLesson_KeepsRepositoryScope_NotForcedGlobal()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(Verdict(confidence: 0.9), cwd: "/repo/project"));

        var lesson = Assert.Single(result.Captured);
        Assert.Equal("Repository:project", lesson.ScopeLabel);
    }

    // ---- Cross-turn "last" ordering ----------------------------------------------

    // A second, later turn's finalization outranks an earlier one for GetLastAsync, even
    // when both land in the same tick (SQLite CreatedAt resolution) — the Id tie-break must
    // still favor the most recently inserted row.
    [Fact]
    public async Task GetLastAsync_SecondTurn_OutranksFirst_AcrossDistinctTurns()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await Finalize(db, Turn(Verdict(confidence: 0.9), prompt: "first turn"));
        var second = await Finalize(db, Turn(Verdict(confidence: 0.9), prompt: "second turn"));

        await using var scope = db.CreateScope();
        var finalizer = scope.ServiceProvider.GetRequiredService<ITurnFinalizer>();
        var last = await finalizer.GetLastAsync();

        Assert.NotNull(last);
        Assert.Equal(second.TurnId, last!.TurnId);
    }

    // When the judge is unavailable (no supplied judgment), the recorded Decision/JudgeReason
    // are empty and must surface as null on the result — not as an empty string.
    [Fact]
    public async Task JudgeUnavailable_DecisionAndJudgeReason_AreNull()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(judgment: null, prompt: "one turn"));

        Assert.Null(result.Decision);
        Assert.Null(result.JudgeReason);
        Assert.Contains(result.Skipped, s => s.Reason == TurnFinalizer.NoJudgmentSuppliedMessage);
    }

    // A judged verdict's Decision/JudgeReason are non-null and carry the judge's actual values.
    [Fact]
    public async Task JudgedVerdict_DecisionAndJudgeReason_AreNonNull()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Finalize(db, Turn(Verdict(reason: JudgeCaptureReason.ReviewerCorrection)));

        Assert.Equal("Capture", result.Decision);
        Assert.Equal("ReviewerCorrection", result.JudgeReason);
    }

    // A Repository-scoped rule with no scope value renders the bare level name, not a
    // "Repository:" with a dangling colon — the Global short-circuit is distinct from this.
    [Fact]
    public async Task RepositoryScope_NoScopeValue_RendersBareLevelName()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var finalizer = scope.ServiceProvider.GetRequiredService<ITurnFinalizer>();

        var input = new TurnFinalizationInput
        {
            Prompt = "one turn",
            Cwd = "/repo/project",
            Source = "stop_hook",
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = null,
            SuppliedJudgment = Verdict(confidence: 0.9),
        };

        var result = await finalizer.FinalizeAsync(input);

        var lesson = Assert.Single(result.Captured);
        Assert.Equal("Repository", lesson.ScopeLabel);
    }

    // Tags from the judge are deduplicated case-insensitively, and the finalizer's own
    // source tag is always present (and first).
    [Fact]
    public async Task CapturedRule_Tags_DedupeCaseInsensitively_AndAlwaysIncludeSourceTag()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var rule = Rule(tags: ["Payments", "payments", "PAYMENTS", "billing"]);
        var result = await Finalize(db, Turn(Verdict(rule: rule)));

        var lesson = Assert.Single(result.Captured);
        var stored = (await Rules(db)).Single(r => r.Id == lesson.RuleId);
        var tags = stored.Tags.Split(',');

        Assert.Equal(TurnFinalizer.SourceTag, tags[0]);
        // Only one casing of "payments" survives, plus the distinct "billing" tag.
        Assert.Equal(3, tags.Length);
        Assert.Single(tags, t => string.Equals(t, "Payments", StringComparison.OrdinalIgnoreCase));
    }

    // The idempotency hash folds in the assistant response too: a turn that differs only in
    // that field is not treated as a replay of a prior turn with the same prompt/cwd/source.
    [Fact]
    public async Task ComputeHash_DiffersOnAssistantResponse_NotTreatedAsReplay()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var withoutAssistant = Turn(Verdict(confidence: 0.9), prompt: "same turn", assistant: null);
        var withAssistant = Turn(Verdict(confidence: 0.9), prompt: "same turn", assistant: "distinct response text");

        await Finalize(db, withoutAssistant);
        var second = await Finalize(db, withAssistant);

        Assert.False(second.FromCache);
    }

    // A turn finalization that was recorded with blank optional fields (a shape that can arise
    // from data written before some field existed, or a partial/legacy row) reconstructs those
    // fields as null, not as an empty string that would render literally.
    [Fact]
    public async Task GetLastAsync_BlankOptionalFields_ReconstructAsNull()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using (var scope = db.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITurnFinalizationRepository>();
            await repo.AddAsync(new TurnFinalization
            {
                Cwd = "/repo/project",
                Source = "stop_hook",
                RawHash = "irrelevant-hash-for-this-row",
                TurnId = string.Empty,
                DecisionSource = string.Empty,
                JudgeDecision = string.Empty,
                JudgeCaptureReason = string.Empty,
            });
        }

        await using var scope2 = db.CreateScope();
        var finalizer = scope2.ServiceProvider.GetRequiredService<ITurnFinalizer>();
        var last = await finalizer.GetLastAsync();

        Assert.NotNull(last);
        Assert.Null(last!.TurnId);
        Assert.Null(last.DecisionSource);
        Assert.Null(last.Decision);
        Assert.Null(last.JudgeReason);
        Assert.Null(last.JudgeConfidence);
        Assert.Null(last.TargetRuleId);
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

    [Fact]
    public void TurnFinalizationInput_Source_DefaultsToManual()
    {
        Assert.Equal("manual", new TurnFinalizationInput().Source);
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
