using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecall.Cli;
using AgentRecall.Cli.Devcontainer;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Finalization;
using AgentRecall.Core.Summary;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Turn Memory Summary: at the end of a turn AgentRecall shows one aggregated view of what
/// it did — used / captured / suggested / skipped, plus remember/ignore and errors. These
/// tests prove the aggregation is turn-scoped (never unrelated old activity), built from
/// structured records (not parsed notices), bounded, and configurable — and that the CLI
/// and Stop-hook surfaces behave per <c>TurnSummaryLevel</c>. All offline and isolated.
/// </summary>
[Collection("ConsoleStdin")]
public class TurnSummaryTests
{
    private const string Badge = "🧠 **AgentRecall:**";

    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    // ---- Helpers: seed rules, activities, finalizations directly --------------

    private static async Task<int> AddRule(
        TestDatabase db,
        string trigger,
        string ruleText,
        CaptureReason reason = CaptureReason.None,
        RuleCategory category = RuleCategory.Unknown)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var added = await repo.AddAsync(new RecallRule
        {
            Trigger = trigger,
            RuleText = ruleText,
            CaptureReason = reason,
            Category = category,
            Status = RuleStatus.Active,
            Confidence = 0.9,
        });
        return added.Id;
    }

    private static async Task AddActivity(
        TestDatabase db,
        ActivityType type,
        string? turnId,
        IEnumerable<int>? ruleIds = null,
        DateTimeOffset createdAt = default,
        string summary = "",
        string? details = null)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentRecallActivityRepository>();
        await repo.AddAsync(new AgentRecallActivity
        {
            ActivityType = type,
            TurnId = turnId,
            RuleIds = ruleIds is null ? null : string.Join(',', ruleIds),
            CreatedAt = createdAt,
            Summary = summary,
            Details = details,
            Source = "test",
        });
    }

    private static async Task AddFinalization(
        TestDatabase db,
        string turnId,
        IEnumerable<int>? captured = null,
        IEnumerable<int>? suggested = null,
        IEnumerable<string>? skipped = null,
        string? error = null,
        DateTimeOffset createdAt = default,
        string decisionSource = "")
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITurnFinalizationRepository>();
        await repo.AddAsync(new TurnFinalization
        {
            TurnId = turnId,
            Source = "test",
            CreatedAt = createdAt,
            CapturedRuleIds = captured is null ? string.Empty : string.Join(',', captured),
            SuggestedRuleIds = suggested is null ? string.Empty : string.Join(',', suggested),
            SkippedReasons = skipped is null ? string.Empty : string.Join('\n', skipped),
            ErrorSummary = error ?? string.Empty,
            DecisionSource = decisionSource,
        });
    }

    /// <summary>Records a finalized turn the way the real path does: a finalization plus a TurnFinalized activity.</summary>
    private static async Task RecordFinalizedTurn(
        TestDatabase db,
        string turnId,
        IEnumerable<int>? captured = null,
        IEnumerable<int>? suggested = null,
        IEnumerable<string>? skipped = null,
        string? error = null)
    {
        await AddFinalization(db, turnId, captured, suggested, skipped, error);
        await AddActivity(db, ActivityType.TurnFinalized, turnId, captured, summary: "finalized turn");
    }

    private static async Task<TurnSummary> BuildLast(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ITurnSummaryService>().BuildLastAsync();
    }

    private static async Task<TurnSummary> BuildForTurn(TestDatabase db, string turnId)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ITurnSummaryService>().BuildForTurnAsync(turnId);
    }

    private static TurnSummaryRule Rule(int id, string title) => new() { Id = id, Title = title };

    // ---- A,B,AA,AB: configuration -------------------------------------------

    [Fact] // A. Default TurnSummaryLevel is Compact.
    public void DefaultTurnSummaryLevel_IsCompact() =>
        Assert.Equal(TurnSummaryLevel.Compact, new AgentRecallOptions().ResolvedTurnSummaryLevel);

    [Fact] // Invalid value falls back to Compact, and validation is exposed.
    public void InvalidTurnSummaryLevel_FallsBackToCompact()
    {
        var options = new AgentRecallOptions { TurnSummaryLevel = "loud-please" };
        Assert.Equal(TurnSummaryLevel.Compact, options.ResolvedTurnSummaryLevel);
        Assert.False(TurnSummaryLevels.IsValid("loud-please"));
        Assert.True(TurnSummaryLevels.IsValid("Detailed"));
        Assert.True(TurnSummaryLevels.IsValid(null));
    }

    [Fact] // AA + AB. The new setting is independent of the existing notice/mode settings.
    public void TurnSummaryLevel_IsIndependentOfNoticeAndInteractiveSettings()
    {
        var options = new AgentRecallOptions { TurnSummaryLevel = "Silent" };
        Assert.Equal(TurnSummaryLevel.Silent, options.ResolvedTurnSummaryLevel);
        // Setting TurnSummaryLevel does not disturb the other, independent settings.
        Assert.Equal(NoticeLevel.Verbose, options.ResolvedActivityNoticeLevel);
        Assert.Equal(InteractiveMemoryMode.Auto, options.ResolvedInteractiveMemoryMode);
    }

    // ---- C,D,E,U,Z: renderer ------------------------------------------------

    [Fact] // C. Compact mode prints one-line aggregate summary.
    public void Compact_PrintsOneAggregateLine()
    {
        var summary = new TurnSummary
        {
            TurnId = "abc123",
            Used = [Rule(12, "a"), Rule(18, "b"), Rule(20, "c"), Rule(21, "d"), Rule(22, "e")],
            Captured = [Rule(28, "x")],
            Skipped = [new TurnSummarySkip { Reason = "Not reusable enough" }],
        };

        var line = TurnSummaryRenderer.RenderCompact(summary);
        Assert.Equal($"{Badge} used 5 rules; auto-captured 1, suggested 0, skipped 1.", line);
        Assert.DoesNotContain('\n', line); // genuinely one line when there are no errors
    }

    [Fact] // D. Detailed mode prints grouped sections.
    public void Detailed_PrintsGroupedSections()
    {
        var summary = new TurnSummary
        {
            Used = [Rule(12, "Scope-safe validators")],
            Captured = [Rule(28, "Preserve else semantics when flattening conditionals")],
            Suggested = [Rule(31, "Avoid duplicate DB reads")],
            Skipped = [new TurnSummarySkip { Reason = "not reusable enough" }],
            Remembered = [Rule(31, "Avoid duplicate DB reads")],
            Ignored = [Rule(32, "Naming preference for one file")],
            Errors = ["finalize-turn could not record capture status. See logs."],
        };

        var text = TurnSummaryRenderer.RenderDetailed(summary);
        Assert.Contains(TurnSummaryRenderer.DetailedHeader, text, StringComparison.Ordinal);
        Assert.Contains("Used:", text, StringComparison.Ordinal);
        Assert.Contains("Auto-captured:", text, StringComparison.Ordinal);
        Assert.Contains("Suggested:", text, StringComparison.Ordinal);
        Assert.Contains("Skipped:", text, StringComparison.Ordinal);
        Assert.Contains("Remembered:", text, StringComparison.Ordinal);
        Assert.Contains("Ignored:", text, StringComparison.Ordinal);
        Assert.Contains("Errors:", text, StringComparison.Ordinal);
        // Suggested carries an approve hint.
        Assert.Contains("agentrecall rules approve 31", text, StringComparison.Ordinal);
    }

    [Fact] // E. Detailed mode limits each section to max 5 items.
    public void Detailed_LimitsEachSectionToFive()
    {
        var used = Enumerable.Range(1, 7).Select(i => Rule(i, $"rule {i}")).ToList();
        var summary = new TurnSummary { Used = used };

        var text = TurnSummaryRenderer.RenderDetailed(summary);
        var usedSection = text[text.IndexOf("Used:", StringComparison.Ordinal)..];
        // Only five "- #" bullets, then an overflow line for the remaining two.
        var bullets = usedSection.Split("\n- #", StringSplitOptions.None).Length - 1;
        Assert.Equal(5, bullets);
        Assert.Contains("…and 2 more", text, StringComparison.Ordinal);
    }

    [Fact] // A standing (always-apply) captured rule renders a [standing] marker.
    public void Detailed_StandingRule_ShowsMarker()
    {
        var summary = new TurnSummary
        {
            Captured = [new TurnSummaryRule { Id = 7, Title = "Keep comments minimal", Standing = true }],
        };

        var text = TurnSummaryRenderer.RenderDetailed(summary);
        Assert.Contains("#7 Keep comments minimal [standing]", text, StringComparison.Ordinal);
    }

    [Fact] // U. Empty summary renders the "no memory activity" message.
    public void Empty_RendersNoActivityMessage()
    {
        var empty = new TurnSummary();
        Assert.True(empty.IsEmpty);
        Assert.Equal($"{Badge} no memory activity recorded for the last turn.",
            TurnSummaryRenderer.RenderCompact(empty, "the last turn"));
        // Detailed degrades to the same single line when there is nothing to group.
        Assert.Equal($"{Badge} no memory activity recorded for this turn.",
            TurnSummaryRenderer.RenderDetailed(empty));
    }

    [Fact] // Silent renders nothing.
    public void Silent_RendersNull() =>
        Assert.Null(TurnSummaryRenderer.Render(new TurnSummary { Captured = [Rule(1, "x")] }, TurnSummaryLevel.Silent));

    [Fact] // Errors appear in the compact line and as bullets.
    public void Compact_WithErrors_AppendsErrorLine()
    {
        var summary = new TurnSummary
        {
            Used = [Rule(1, "a"), Rule(2, "b"), Rule(3, "c")],
            Suggested = [Rule(9, "s")],
            Errors = ["finalize-turn could not record capture status. See logs."],
        };

        var line = TurnSummaryRenderer.RenderCompact(summary);
        Assert.Contains("used 3 rules; auto-captured 0, suggested 1, skipped 0, errors 1.", line, StringComparison.Ordinal);
        Assert.Contains("- Error: finalize-turn could not record capture status. See logs.", line, StringComparison.Ordinal);
    }

    // ---- I,J,K,L,M,N: the summary aggregates the turn's structured activity ----

    [Fact] // I. Summary includes retrieved/used rules from the same turn.
    public async Task Summary_IncludesUsedRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var r = await AddRule(db, "writing validators", "Scope-safe validators");
        await AddActivity(db, ActivityType.ContextFetched, "turn-1", [r]);

        var summary = await BuildForTurn(db, "turn-1");
        Assert.Single(summary.Used);
        Assert.Equal(r, summary.Used[0].Id);
        Assert.Equal("writing validators", summary.Used[0].Title);
    }

    [Fact] // J. Summary includes captured rules from the same turn.
    public async Task Summary_IncludesCapturedRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var r = await AddRule(db, "flattening conditionals", "Preserve else semantics",
            reason: CaptureReason.ObservedAgentFailure, category: RuleCategory.EngineeringLesson);
        await RecordFinalizedTurn(db, "turn-2", captured: [r]);

        var summary = await BuildForTurn(db, "turn-2");
        Assert.Single(summary.Captured);
        Assert.Equal(r, summary.Captured[0].Id);
        // Captured rules carry the capture evidence as the reason (structured, not parsed).
        Assert.Equal(nameof(CaptureReason.ObservedAgentFailure), summary.Captured[0].Reason);
    }

    // J2. A model-supplied judgment for a turn still wins the summary even when a later
    // "judge unavailable" finalization was recorded for the same turn (the native Stop hook
    // firing with no judgment) — the real judged decision must not be buried.
    [Fact]
    public async Task Summary_PrefersJudgedFinalizationOverLaterUnavailableForSameTurn()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var r = await AddRule(db, "flattening conditionals", "Preserve else semantics",
            reason: CaptureReason.ObservedAgentFailure, category: RuleCategory.EngineeringLesson);

        await AddFinalization(db, "turn-2b", captured: [r],
            decisionSource: TurnFinalizer.JudgeDecisionSource, createdAt: DateTimeOffset.UtcNow);
        await AddFinalization(db, "turn-2b", skipped: ["Semantic capture judge unavailable; no automatic capture performed."],
            createdAt: DateTimeOffset.UtcNow.AddSeconds(1));

        var summary = await BuildForTurn(db, "turn-2b");
        Assert.Single(summary.Captured);
        Assert.Equal(r, summary.Captured[0].Id);
        Assert.Empty(summary.Skipped);
    }

    [Fact] // K. Summary includes suggested pending rules from the same turn.
    public async Task Summary_IncludesSuggestedRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var r = await AddRule(db, "duplicate reads", "Avoid duplicate DB reads");
        await RecordFinalizedTurn(db, "turn-3", suggested: [r]);

        var summary = await BuildForTurn(db, "turn-3");
        Assert.Single(summary.Suggested);
        Assert.Equal(r, summary.Suggested[0].Id);
    }

    [Fact] // L. Summary includes skipped candidates from the same turn.
    public async Task Summary_IncludesSkippedCandidates()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await RecordFinalizedTurn(db, "turn-4", skipped: ["Generic refactoring: not reusable enough"]);

        var summary = await BuildForTurn(db, "turn-4");
        Assert.Single(summary.Skipped);
        Assert.Equal("Generic refactoring: not reusable enough", summary.Skipped[0].Reason);
    }

    [Fact] // M. Summary includes remembered/ignored Interactive Memory decisions.
    public async Task Summary_IncludesRememberedAndIgnored()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var remembered = await AddRule(db, "remember me", "Remembered rule");
        var ignored = await AddRule(db, "ignore me", "Ignored rule");
        await AddActivity(db, ActivityType.SuggestionRemembered, "turn-5", [remembered]);
        await AddActivity(db, ActivityType.SuggestionIgnored, "turn-5", [ignored]);

        var summary = await BuildForTurn(db, "turn-5");
        Assert.Equal(remembered, Assert.Single(summary.Remembered).Id);
        Assert.Equal(ignored, Assert.Single(summary.Ignored).Id);
    }

    [Fact] // N. Summary includes recoverable errors.
    public async Task Summary_IncludesErrors()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await RecordFinalizedTurn(db, "turn-6", error: "finalize-turn could not record capture status. See logs.");

        var summary = await BuildForTurn(db, "turn-6");
        Assert.Contains("finalize-turn could not record capture status. See logs.", summary.Errors);
    }

    // ---- O,P,Q: turn correlation / scoping ----------------------------------

    [Fact] // O + P. Summary uses the turn id and excludes unrelated other-turn activity.
    public async Task Summary_IsScopedToTurnId_ExcludingOtherTurns()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var a = await AddRule(db, "turn a rule", "A");
        var b = await AddRule(db, "turn b rule", "B");
        await AddActivity(db, ActivityType.ContextFetched, "turn-a", [a]);
        await AddActivity(db, ActivityType.ContextFetched, "turn-b", [b]); // newer turn

        // BuildLast anchors on the most recent activity that carries a turn id (turn-b).
        var last = await BuildLast(db);
        Assert.Equal("turn-b", last.TurnId);
        Assert.Equal(b, Assert.Single(last.Used).Id);

        // Asking for turn-a explicitly returns only turn-a's data.
        var first = await BuildForTurn(db, "turn-a");
        Assert.Equal(a, Assert.Single(first.Used).Id);
    }

    [Fact] // Q. With no turn id, the timestamp fallback is conservative (short window).
    public async Task Summary_TimestampFallback_IsConservative()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var recent = await AddRule(db, "recent rule", "Recent");
        var old = await AddRule(db, "old rule", "Old");

        var now = DateTimeOffset.UtcNow;
        // Insert the old activity first (lower id), the recent one last (anchor).
        await AddActivity(db, ActivityType.ContextFetched, turnId: null, [old], createdAt: now - TimeSpan.FromHours(1));
        await AddActivity(db, ActivityType.ContextFetched, turnId: null, [recent], createdAt: now);

        var summary = await BuildLast(db);
        Assert.Null(summary.TurnId); // fell back to a time window, not an id
        Assert.Equal(recent, Assert.Single(summary.Used).Id); // the hour-old activity is excluded
    }

    [Fact] // Empty database yields an empty summary, not a crash.
    public async Task Summary_EmptyDatabase_IsEmpty()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var summary = await BuildLast(db);
        Assert.True(summary.IsEmpty);
        Assert.Null(summary.TurnId);
    }

    // ---- R,S,T,U: CLI command -----------------------------------------------

    [Fact] // R. `agentrecall turn-summary --last` works.
    public async Task Cli_TurnSummaryLast_PrintsCompactByDefault()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var r = await AddRule(db, "validators", "Scope-safe validators");
        await AddActivity(db, ActivityType.ContextFetched, "turn-cli", [r]);
        await RecordFinalizedTurn(db, "turn-cli", captured: [r]);

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["turn-summary", "--last"], db.Services, output);

        Assert.Equal(0, code);
        Assert.Contains(Badge, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("captured 1", output.ToString(), StringComparison.Ordinal);
    }

    [Fact] // S. `--json` returns valid, deterministic JSON.
    public async Task Cli_TurnSummaryLast_Json_IsValidAndStable()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var used = await AddRule(db, "validators", "Scope-safe validators");
        var captured = await AddRule(db, "flatten", "Preserve else semantics",
            reason: CaptureReason.ObservedAgentFailure);
        await AddActivity(db, ActivityType.ContextFetched, "turn-json", [used]);
        await RecordFinalizedTurn(db, "turn-json", captured: [captured]);

        var first = new StringWriter();
        var code = await CommandRouter.RunAsync(["turn-summary", "--last", "--json"], db.Services, first);
        Assert.Equal(0, code);

        var node = JsonNode.Parse(first.ToString())!;
        Assert.Equal("turn-json", node["turn_id"]!.GetValue<string>());
        Assert.Equal(1, node["summary"]!["used"]!.GetValue<int>());
        Assert.Equal(1, node["summary"]!["captured"]!.GetValue<int>());
        Assert.Equal(used, node["used_rules"]![0]!["id"]!.GetValue<int>());
        Assert.Equal(captured, node["captured_rules"]![0]!["id"]!.GetValue<int>());
        Assert.Equal(nameof(CaptureReason.ObservedAgentFailure),
            node["captured_rules"]![0]!["reason"]!.GetValue<string>());

        // Deterministic: a second run produces byte-identical JSON.
        var second = new StringWriter();
        await CommandRouter.RunAsync(["turn-summary", "--last", "--json"], db.Services, second);
        Assert.Equal(first.ToString(), second.ToString());
    }

    [Fact] // T. `--detailed` prints grouped details.
    public async Task Cli_TurnSummaryLast_Detailed_PrintsSections()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var r = await AddRule(db, "validators", "Scope-safe validators");
        await AddActivity(db, ActivityType.ContextFetched, "turn-det", [r]);
        await RecordFinalizedTurn(db, "turn-det", captured: [r]);

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["turn-summary", "--last", "--detailed"], db.Services, output);

        Assert.Equal(0, code);
        Assert.Contains(TurnSummaryRenderer.DetailedHeader, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Used:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Auto-captured:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact] // U. An empty last turn prints "no memory activity recorded".
    public async Task Cli_TurnSummaryLast_Empty_PrintsNoActivity()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["turn-summary", "--last"], db.Services, output);

        Assert.Equal(0, code);
        Assert.Contains("no memory activity recorded for the last turn", output.ToString(), StringComparison.Ordinal);
    }

    [Fact] // Empty JSON still carries the stable shape with zeroed counts.
    public async Task Cli_TurnSummaryLast_EmptyJson_HasStableShape()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var output = new StringWriter();
        await CommandRouter.RunAsync(["turn-summary", "--last", "--json"], db.Services, output);

        var node = JsonNode.Parse(output.ToString())!;
        Assert.Equal(0, node["summary"]!["used"]!.GetValue<int>());
        Assert.Equal(0, node["summary"]!["captured"]!.GetValue<int>());
        Assert.Equal(0, node["summary"]!["errors"]!.GetValue<int>());
        Assert.Empty(node["used_rules"]!.AsArray());
        Assert.Empty(node["captured_rules"]!.AsArray());
    }

    // ---- F,G,H,Z: hooks ------------------------------------------------------

    [Fact] // F + I (end-to-end). UserPromptSubmit records used-rule activity stamped with the turn id.
    public async Task UserPromptSubmit_RecordsUsedRulesActivityWithTurnId()
    {
        using var repo = new TempRepo();
        await using var db = new TestDatabase();
        await Init(db);
        await SeedActiveRule(db, "writing Moq unit tests",
            "Always use It.IsAny<T>() matchers when the argument value is not important.",
            "moq,tests,testing,matchers");

        var prompt = "Write unit tests for OrderService using Moq";
        var originalIn = Console.In;
        try
        {
            Console.SetIn(new StringReader(Payload(prompt, repo.Path)));
            var code = await CommandRouter.RunAsync(["hook", "user-prompt-submit"], db.Services, new StringWriter());
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        var turnId = TurnCorrelation.Compute(repo.Path, prompt);
        Assert.NotNull(turnId);

        await using var scope = db.CreateScope();
        var activities = await scope.ServiceProvider
            .GetRequiredService<IAgentRecallActivityRepository>().ListRecentAsync(20);
        var fetched = activities.FirstOrDefault(a => a.ActivityType == ActivityType.ContextFetched);
        Assert.NotNull(fetched);
        Assert.Equal(turnId, fetched!.TurnId);
    }

    [Fact] // G + H + I + Z. Stop/finalize-turn prints the summary, joining used + captured by turn id, and never blocks.
    public async Task FinalizeTurnHook_PrintsTurnSummary_JoiningUsedAndCaptured()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var prompt = "We do not mock DbContext directly.";
        var cwd = "/repo/project";
        var turnId = TurnCorrelation.Compute(cwd, prompt)!;

        // Simulate the rules used earlier in the same turn (as UserPromptSubmit would record).
        var usedRule = await AddRule(db, "writing tests", "Some long used rule body that should never appear verbatim in compact output.");
        await AddActivity(db, ActivityType.ContextFetched, turnId, [usedRule]);

        var originalIn = Console.In;
        var output = new StringWriter();
        try
        {
            var payload = new JsonObject
            {
                ["prompt"] = prompt,
                ["cwd"] = cwd,
                ["judgment"] = new JsonObject
                {
                    ["decision"] = "Capture",
                    ["memory_type"] = "RepositoryConvention",
                    ["confidence"] = 0.9,
                    ["capture_reason"] = "RepositoryConvention",
                    ["normalized_rule"] = new JsonObject
                    {
                        ["title"] = "Do not mock DbContext",
                        ["condition"] = "when writing tests that touch the database",
                        ["action"] = "use a real SQLite context instead of mocking DbContext",
                        ["because"] = "mocking DbContext hides query bugs",
                        ["scope"] = "project",
                    },
                },
            };
            Console.SetIn(new StringReader(payload.ToJsonString()));
            var code = await CommandRouter.RunAsync(["finalize-turn", "--hook"], db.Services, output);
            Assert.Equal(0, code); // H. never blocks
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        var message = JsonNode.Parse(output.ToString().Trim())!["systemMessage"]!.GetValue<string>();
        Assert.Contains(Badge, message, StringComparison.Ordinal);
        Assert.Contains("used 1 rule", message, StringComparison.Ordinal); // joined retrieval
        Assert.Contains("captured 1", message, StringComparison.Ordinal);  // joined capture
        // Z. Compact hook output never carries full rule bodies.
        Assert.DoesNotContain("should never appear verbatim", message, StringComparison.Ordinal);
    }

    [Fact] // B. Silent mode prints no automatic end-of-turn summary at the Stop hook.
    public async Task FinalizeTurnHook_Silent_PrintsNothing()
    {
        await using var db = new TestDatabase(o => o.TurnSummaryLevel = "Silent");
        await Init(db);

        var originalIn = Console.In;
        var output = new StringWriter();
        try
        {
            Console.SetIn(new StringReader(new JsonObject
            {
                ["prompt"] = "We do not mock DbContext directly.",
                ["cwd"] = "/repo/project",
            }.ToJsonString()));
            var code = await CommandRouter.RunAsync(["finalize-turn", "--hook"], db.Services, output);
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        Assert.True(string.IsNullOrWhiteSpace(output.ToString()));
    }

    [Fact] // Silent mode still lets the status/turn-summary commands report data.
    public async Task SilentMode_StatusCommandsStillWork()
    {
        await using var db = new TestDatabase(o => o.TurnSummaryLevel = "Silent");
        await Init(db);
        var r = await AddRule(db, "validators", "Scope-safe validators");
        await AddActivity(db, ActivityType.ContextFetched, "turn-silent", [r]);
        await RecordFinalizedTurn(db, "turn-silent", captured: [r]);

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["turn-summary", "--last"], db.Services, output);
        Assert.Equal(0, code);
        // An explicit command is never a no-op even when the configured level is Silent.
        Assert.Contains(Badge, output.ToString(), StringComparison.Ordinal);
    }

    // ---- V: capture-status pointer ------------------------------------------

    [Fact] // V. capture-status points to turn-summary but does not duplicate the full summary.
    public async Task CaptureStatus_PointsToTurnSummary_WithoutDuplicatingIt()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await RecordFinalizedTurn(db, "turn-ptr", captured: [await AddRule(db, "t", "Captured rule")]);

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["capture-status", "--last-turn"], db.Services, output);

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("agentrecall turn-summary --last", text, StringComparison.Ordinal);
        // It still answers the capture question, but does not render the aggregate summary line.
        Assert.Contains("Captured:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("used 0 rules", text, StringComparison.Ordinal);
    }

    // ---- W,X,Y: documentation -----------------------------------------------

    [Fact] // W. CLAUDE.md scaffold includes Turn Summary guidance and forbids manual-call / may-have wording.
    public void Scaffold_IncludesTurnSummaryGuidance()
    {
        var guidance = DevcontainerScaffolder.ClaudeMdGuidance;
        Assert.Contains("Turn Memory Summary", guidance, StringComparison.Ordinal);
        Assert.Contains("agentrecall turn-summary --last", guidance, StringComparison.Ordinal);
        Assert.Contains("source of truth", guidance, StringComparison.Ordinal);
        // The forbidden manual-call / may-have wording is present as guidance not to use it.
        Assert.Contains("I didn't manually save anything.", guidance, StringComparison.Ordinal);
        Assert.Contains("The hook may have captured it.", guidance, StringComparison.Ordinal);
    }

    [Fact] // X + Y. README documents the Turn Memory Summary and lists the command.
    public void Readme_DocumentsTurnMemorySummary()
    {
        var readme = File.ReadAllText(FindRepoFile("README.md"));
        Assert.Contains("## Turn Memory Summary", readme, StringComparison.Ordinal);
        Assert.Contains("AgentRecall.TurnSummaryLevel", readme, StringComparison.Ordinal);
        Assert.Contains("turn-summary --last", readme, StringComparison.Ordinal);
        Assert.Contains("turn-summary --last --json", readme, StringComparison.Ordinal);
        Assert.Contains("turn-summary --last --detailed", readme, StringComparison.Ordinal);
    }

    // ---- AD: isolation ------------------------------------------------------

    [Fact] // AD. Tests run against an isolated temp DB and never touch ~/.agentrecall.
    public async Task TestDatabase_IsIsolatedFromUserHome()
    {
        await using var db = new TestDatabase();
        var home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentrecall");
        Assert.StartsWith(Path.GetTempPath(), db.Options.DataDirectory, StringComparison.Ordinal);
        Assert.NotEqual(home, db.Options.DataDirectory);
    }

    // ---- shared helpers -----------------------------------------------------

    private static async Task SeedActiveRule(TestDatabase db, string trigger, string ruleText, string tags)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        await repo.AddAsync(new RecallRule
        {
            Trigger = trigger,
            RuleText = ruleText,
            Tags = tags,
            Confidence = 0.9,
            Status = RuleStatus.Active,
            ScopeLevel = ScopeLevel.Global,
            ScopeValue = "",
        });
    }

    private static string Payload(string prompt, string cwd) =>
        new JsonObject { ["prompt"] = prompt, ["cwd"] = cwd }.ToJsonString();

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
}
