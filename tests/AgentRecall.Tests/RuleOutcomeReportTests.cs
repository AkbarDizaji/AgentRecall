using AgentRecall.Cli;
using AgentRecall.Cli.Mcp.Tools;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Outcomes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// The outcome half of recall. Rules are injected on every turn, but nothing moves their
/// confidence unless someone says how they fared, and AgentRecall cannot observe that itself — so
/// it asks the one party that can, then refuses to take the answer on trust: an outcome counts
/// only for a rule a retrieval actually injected, and only for what an agent genuinely witnesses.
/// Silence is recorded as silence, because an empty ledger and a ledger of honest "ignored"
/// verdicts mean opposite things.
/// </summary>
[Collection("ConsoleStdin")]
public class RuleOutcomeReportTests
{
    private const string Prompt =
        "Rework the importer's retry policy so a transient failure is retried once, then surfaced.";

    private static async Task<TestDatabase> NewDbAsync()
    {
        var db = new TestDatabase();
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        return db;
    }

    /// <summary>Seeds a rule and the injection record a turn would have written for it.</summary>
    private static async Task<(int RuleId, string RetrievalId, string TurnId)> SeedInjectedRuleAsync(
        TestDatabase db, string cwd = "/repo/importer")
    {
        await using var scope = db.CreateScope();
        var rule = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().AddAsync(
            new RecallRule
            {
                Trigger = "importing a batch",
                RuleText = "Retry a transient import failure once, then surface it with its cause.",
                Mistake = "Avoid retrying forever.",
                TechnicalContext = "", Tags = "importer,retry",
                Confidence = 0.6, Status = RuleStatus.Active, ScopeLevel = ScopeLevel.Global, ScopeValue = "",
            });

        var retrievalId = "ret" + Guid.NewGuid().ToString("N")[..8];
        await scope.ServiceProvider.GetRequiredService<IRetrievalRecordRepository>().AddAsync(
            new RetrievalRecord { RetrievalId = retrievalId, Task = Prompt, RuleIds = rule.Id.ToString() });

        var turnId = TurnCorrelation.Compute(cwd, Prompt)!;
        await scope.ServiceProvider.GetRequiredService<IActivityRecorder>().RecordAsync(
            new ActivityNotice
            {
                Type = ActivityType.ContextFetched,
                Summary = "fetched 1 relevant rule.",
                RuleIds = [rule.Id],
                Source = "hook",
                TurnId = turnId,
                OperationHash = $"context:{retrievalId}",
            });

        return (rule.Id, retrievalId, turnId);
    }

    private static string Payload(string? ruleOutcomes = null, string session = "chat-1")
    {
        var outcomes = ruleOutcomes is null ? string.Empty : $",\n  \"rule_outcomes\": {ruleOutcomes}";
        return $$"""
        {
          "cwd": "/repo/importer",
          "session_id": "{{session}}",
          "prompt": "{{Prompt}}",
          "assistant_response": "Reworked the retry policy, added tests for the transient path, suite green.",
          "judgment": {
            "decision": "Skip",
            "memory_type": "NotMemory",
            "capture_reason": "NotReusable",
            "confidence": 0.8,
            "why_not_saved": "Ordinary work; nothing durable."
          }{{outcomes}}
        }
        """;
    }

    private static async Task<(int Code, string Output)> RunAsync(TestDatabase db, string stdin, params string[] args)
    {
        var originalIn = Console.In;
        var writer = new StringWriter();
        try
        {
            Console.SetIn(new StringReader(stdin));
            var code = await CommandRouter.RunAsync(args, db.Services, writer);
            return (code, writer.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    private static async Task<IReadOnlyList<RuleOutcome>> Outcomes(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRuleOutcomeRepository>().ListAsync();
    }

    // 1. A reported outcome for an injected rule moves that rule's confidence and lands in the ledger.
    [Fact]
    public async Task ReportedOutcome_ForAnInjectedRule_MovesConfidenceAndIsRecorded()
    {
        await using var db = await NewDbAsync();
        var (ruleId, retrievalId, turnId) = await SeedInjectedRuleAsync(db);

        await RunAsync(
            db,
            Payload($$"""[{"rule_id": {{ruleId}}, "retrieval_id": "{{retrievalId}}", "outcome": "UserAccepted", "note": "kept as written"}]"""),
            "finalize-turn");

        var outcome = Assert.Single(await Outcomes(db));
        Assert.Equal(ruleId, outcome.RuleId);
        Assert.Equal(OutcomeType.UserAccepted, outcome.Type);
        Assert.Equal(turnId, outcome.TaskId);
        Assert.True(outcome.ConfidenceDelta > 0, "UserAccepted should raise confidence.");
        Assert.Equal("kept as written", outcome.Reason);

        await using var scope = db.CreateScope();
        var rule = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().GetAsync(ruleId);
        Assert.True(rule!.Confidence > 0.6, "the rule's confidence should have moved.");
    }

    // 2. An outcome for a rule no retrieval injected is refused: evidence cannot be invented for a
    //    rule that was never in play.
    [Fact]
    public async Task ReportedOutcome_ForARuleNeverInjected_IsRefused()
    {
        await using var db = await NewDbAsync();
        await SeedInjectedRuleAsync(db);

        var (_, output) = await RunAsync(
            db,
            Payload("""[{"rule_id": 4242, "outcome": "UserAccepted"}]"""),
            "finalize-turn");

        Assert.Empty(await Outcomes(db));
        Assert.Contains("Outcome refused", output);
    }

    // 3. An outcome the agent cannot witness is refused with the reason, however confidently it is
    //    asserted: a model saying "tests passed" is not a test run.
    [Fact]
    public async Task ReportedOutcome_ThatCannotBeWitnessed_IsRefused()
    {
        await using var db = await NewDbAsync();
        var (ruleId, _, _) = await SeedInjectedRuleAsync(db);

        var (_, output) = await RunAsync(
            db,
            Payload($$"""[{"rule_id": {{ruleId}}, "outcome": "TestsPassed"}]"""),
            "finalize-turn");

        Assert.Empty(await Outcomes(db));
        Assert.Contains("cannot be self-reported", output);
    }

    // 4. RuleIgnored is a first-class answer — the honest verdict for a rule that did not apply.
    [Fact]
    public async Task ReportedOutcome_RuleIgnored_IsRecordedLikeAnyOther()
    {
        await using var db = await NewDbAsync();
        var (ruleId, _, _) = await SeedInjectedRuleAsync(db);

        await RunAsync(
            db,
            Payload($$"""[{"rule_id": {{ruleId}}, "outcome": "RuleIgnored", "note": "did not apply here"}]"""),
            "finalize-turn");

        Assert.Equal(OutcomeType.RuleIgnored, Assert.Single(await Outcomes(db)).Type);
    }

    // 5. A turn that used rules and reported nothing is recorded as unreported — silence stays
    //    visible instead of looking like a turn that used no rules at all.
    [Fact]
    public async Task TurnThatReportsNothing_RecordsTheSilence()
    {
        await using var db = await NewDbAsync();
        var (ruleId, _, turnId) = await SeedInjectedRuleAsync(db);

        await RunAsync(db, Payload(), "finalize-turn");

        Assert.Empty(await Outcomes(db));

        await using var scope = db.CreateScope();
        var activities = await scope.ServiceProvider
            .GetRequiredService<IAgentRecallActivityRepository>().ListByTurnAsync(turnId);

        var unreported = Assert.Single(
            activities, a => a.ActivityType == ActivityType.RuleOutcomesUnreported);
        Assert.Contains(ruleId.ToString(), unreported.RuleIds!);
    }

    // 6. The end-of-turn ask names the rules still awaiting an outcome, so the report is asked for
    //    rather than hoped for — and only when the turn actually injected rules.
    [Fact]
    public async Task TheAsk_NamesTheRulesAwaitingAnOutcome()
    {
        await using var db = await NewDbAsync();
        var (ruleId, _, _) = await SeedInjectedRuleAsync(db);

        var unjudged = $$"""
        {
          "cwd": "/repo/importer",
          "session_id": "chat-1",
          "prompt": "{{Prompt}}",
          "assistant_response": "Reworked the importer's retry policy so a transient failure is retried once and then surfaced with its cause, adjusted the surrounding tests, and confirmed the suite passes locally."
        }
        """;

        var (_, output) = await RunAsync(db, unjudged, "finalize-turn", "--hook");
        var reason = System.Text.Json.Nodes.JsonNode.Parse(output.Trim())!["reason"]!.GetValue<string>();

        Assert.Contains("rule_outcomes", reason);
        Assert.Contains($"#{ruleId}", reason);
        Assert.Contains("RuleIgnored", reason);
    }

    // 7. A turn that injected nothing is never asked about outcomes: no rules, no question.
    [Fact]
    public async Task TheAsk_OmitsOutcomes_WhenTheTurnInjectedNothing()
    {
        await using var db = await NewDbAsync();

        var unjudged = $$"""
        {
          "cwd": "/repo/importer",
          "session_id": "chat-1",
          "prompt": "{{Prompt}}",
          "assistant_response": "Reworked the importer's retry policy so a transient failure is retried once and then surfaced with its cause, adjusted the surrounding tests, and confirmed the suite passes locally."
        }
        """;

        var (_, output) = await RunAsync(db, unjudged, "finalize-turn", "--hook");
        var reason = System.Text.Json.Nodes.JsonNode.Parse(output.Trim())!["reason"]!.GetValue<string>();

        Assert.DoesNotContain("rule_outcomes", reason);
    }

    // 8. The tool route reaches the same seam as the payload route: a verdict submitted with
    //    outcomes records both.
    [Fact]
    public async Task SubmitCaptureJudgmentTool_CarriesOutcomesToTheSameSeam()
    {
        await using var db = await NewDbAsync();
        var (ruleId, retrievalId, _) = await SeedInjectedRuleAsync(db);

        var arguments = new System.Text.Json.Nodes.JsonObject
        {
            ["decision"] = "Skip",
            ["memory_type"] = "NotMemory",
            ["capture_reason"] = "NotReusable",
            ["why_not_saved"] = "Ordinary work.",
            ["cwd"] = "/repo/importer",
            ["prompt"] = Prompt,
            ["assistant_response"] = "Reworked the retry policy and adjusted the tests.",
            ["rule_outcomes"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["rule_id"] = ruleId,
                    ["retrieval_id"] = retrievalId,
                    ["outcome"] = "UserRejected",
                    ["note"] = "the rule pointed the wrong way here",
                },
            },
        };

        await using (var scope = db.CreateScope())
        {
            var result = await new SubmitCaptureJudgmentTool()
                .InvokeAsync(arguments, scope.ServiceProvider, default);
            Assert.True(result!["submitted"]!.GetValue<bool>());
        }

        var outcome = Assert.Single(await Outcomes(db));
        Assert.Equal(OutcomeType.UserRejected, outcome.Type);
        Assert.True(outcome.ConfidenceDelta < 0, "UserRejected should lower confidence.");
    }
}
