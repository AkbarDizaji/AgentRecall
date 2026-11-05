using AgentRecall.Cli;
using AgentRecall.Cli.Hooks;
using AgentRecall.Cli.Mcp.Tools;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class GlossaryTests
{
    private static GlossaryTerm Term(string term, string canonical) =>
        new() { Term = Glossary.Normalize(term), Canonical = Glossary.Normalize(canonical) };

    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task AddTerm(TestDatabase db, string term, string canonical)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IGlossaryTermRepository>().AddAsync(Term(term, canonical));
    }

    private static async Task SeedEventRule(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().AddAsync(new RecallRule
        {
            Trigger = "changing event handlers",
            RuleText = "Publish an event only after the transaction commits.",
            Mistake = "", TechnicalContext = "", Tags = "event,events,transactions",
            Confidence = 0.9, Status = RuleStatus.Promoted, ScopeLevel = ScopeLevel.Global, ScopeValue = "",
        });
    }

    private static async Task<(int Code, string Output)> RunAsync(TestDatabase db, params string[] args)
    {
        var writer = new StringWriter();
        var code = await CommandRouter.RunAsync(args, db.Services, writer);
        return (code, writer.ToString());
    }

    private static string Payload(string prompt) =>
        $$"""{"prompt": {{System.Text.Json.JsonSerializer.Serialize(prompt)}}, "cwd": "/tmp/project"}""";

    // ---- Matching -------------------------------------------------------------

    [Fact]
    public void Normalize_FoldsArabicLettersAndMarks_SoKeyboardsAgree()
    {
        // Arabic yeh/kaf and a combining mark are how the same Persian word often arrives.
        Assert.Equal(Glossary.Normalize("کیک"), Glossary.Normalize("كيك"));
        Assert.Equal(Glossary.Normalize("ایونت"), Glossary.Normalize("ایونتَ"));
    }

    [Fact]
    public void Translate_ReplacesTheWord_AndLeavesNoUntranslatedCopy()
    {
        var translated = Glossary.Translate("ایونت رو درست کن", [Term("ایونت", "event")]);

        Assert.Contains("event", translated);
        Assert.DoesNotContain("ایونت", translated);
    }

    [Fact]
    public void Translate_MatchesWholeWordsOnly()
    {
        // "test" must not fire inside "latest".
        Assert.Equal("the latest build", Glossary.Translate("the latest build", [Term("test", "unit test")]));
    }

    [Fact]
    public void Translate_MatchesAWordJoinedToItsSuffix()
    {
        // A zero-width non-joiner separates a Persian plural suffix: "ایونت‌ها" is "ایونت" + "ها".
        Assert.Contains("event", Glossary.Translate("ایونت‌ها", [Term("ایونت", "event")]));
    }

    [Fact]
    public void Translate_PrefersTheLongerPhrase()
    {
        var terms = new[] { Term("گیت", "gate"), Term("فیچر گیت", "feature gate") };

        Assert.Equal("feature gate", Glossary.Translate("فیچر گیت", terms));
    }

    [Fact]
    public void Translate_WithoutAMatch_ReturnsTheTextUnchanged() =>
        Assert.Equal("Fix the OrderService", Glossary.Translate("Fix the OrderService", [Term("ایونت", "event")]));

    // ---- Recall ---------------------------------------------------------------

    [Fact]
    public async Task Injection_PersianTask_ReachesTheEnglishRule_OnlyThroughTheGlossary()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedEventRule(db);

        async Task<ContextInjectionResult> Build()
        {
            await using var scope = db.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IContextInjectionService>()
                .BuildContextAsync(new ContextRequest { Task = "ایونت" });
        }

        Assert.Empty((await Build()).All);

        await AddTerm(db, "ایونت", "event");

        Assert.Contains((await Build()).All, r => r.Rule.RuleText.Contains("transaction commits"));
    }

    [Fact]
    public async Task Search_PersianQuery_FindsTheEnglishRule()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedEventRule(db);
        await AddTerm(db, "ایونت", "event");

        await using var scope = db.CreateScope();
        var results = await scope.ServiceProvider.GetRequiredService<IRecallSearchService>().SearchAsync("ایونت");

        Assert.Single(results);
    }

    // ---- Prompt gate ----------------------------------------------------------

    [Fact]
    public async Task Hook_PersianPromptNamingAGlossaryTerm_PassesTheGate()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedEventRule(db);

        var prompt = Payload("ایونت رو درست کن");
        Assert.Equal(string.Empty, await UserPromptSubmitHook.RunAsync(prompt, db.Services, new StringWriter()));

        await AddTerm(db, "ایونت", "event");

        Assert.Contains("transaction commits", await UserPromptSubmitHook.RunAsync(prompt, db.Services, new StringWriter()));
    }

    [Fact]
    public async Task Hook_PromptTheGateRejects_StillGetsStandingRules_AndNothingElse()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedEventRule(db);
        await using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().AddAsync(new RecallRule
            {
                Trigger = "answering in Persian", RuleText = "Put English terms on their own line.",
                Mistake = "", TechnicalContext = "", Tags = "persian,formatting",
                Confidence = 0.95, Status = RuleStatus.Active, AlwaysApply = true,
                ScopeLevel = ScopeLevel.Global, ScopeValue = "",
            });
        }

        // No development keyword and no glossary term: only the standing rule may come through.
        var output = await UserPromptSubmitHook.RunAsync(Payload("سادهتر بگو event"), db.Services, new StringWriter());

        Assert.Contains("English terms on their own line", output);
        Assert.DoesNotContain("transaction commits", output);
    }

    // ---- CLI ------------------------------------------------------------------

    [Fact]
    public async Task Cli_AddListUpdateRemove()
    {
        await using var db = new TestDatabase();

        var (addCode, addOut) = await RunAsync(db, "glossary", "add", "ایونت", "Event");
        Assert.Equal(0, addCode);
        Assert.Contains("Added to the glossary:", addOut);

        var (_, listOut) = await RunAsync(db, "glossary", "list");
        Assert.Contains("ایونت", listOut);
        Assert.Contains("-> event", listOut);

        // Re-adding a term re-points it instead of failing or duplicating.
        var (updateCode, updateOut) = await RunAsync(db, "glossary", "add", "ایونت", "domain event");
        Assert.Equal(0, updateCode);
        Assert.Contains("Updated in the glossary:", updateOut);
        var (_, jsonOut) = await RunAsync(db, "glossary", "list", "--json");
        Assert.Contains("domain event", jsonOut);
        Assert.Single(System.Text.Json.JsonDocument.Parse(jsonOut).RootElement.EnumerateArray());

        var (removeCode, _) = await RunAsync(db, "glossary", "remove", "ایونت");
        Assert.Equal(0, removeCode);
        var (missingCode, _) = await RunAsync(db, "glossary", "remove", "ایونت");
        Assert.Equal(1, missingCode);
    }

    [Fact]
    public async Task Cli_MissingArguments_PrintsUsage()
    {
        await using var db = new TestDatabase();

        var (code, output) = await RunAsync(db, "glossary", "add", "ایونت");

        Assert.Equal(1, code);
        Assert.Contains("agentrecall glossary add", output);
    }

    // ---- Captured with a rule -------------------------------------------------

    private static async Task<JsonNode> Judge(TestDatabase db, JsonObject verdict)
    {
        await using var scope = db.CreateScope();
        return await new SubmitCaptureJudgmentTool().InvokeAsync(verdict, scope.ServiceProvider, default);
    }

    private static JsonObject EventRule(JsonObject glossary) => new()
    {
        ["title"] = "Publish events after commit",
        ["condition"] = "when changing event handlers",
        ["action"] = "publish an event only after the transaction commits",
        ["because"] = "a rolled-back transaction must not have announced anything",
        ["scope"] = "repository",
        ["glossary"] = glossary,
    };

    [Fact]
    public async Task JudgedCapture_KeepsTheUsersWordsWithTheRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var result = await Judge(db, new JsonObject
        {
            ["decision"] = "Capture",
            ["memory_type"] = "EngineeringLesson",
            ["capture_reason"] = "UserCorrection",
            ["confidence"] = 0.9,
            ["prompt"] = "ایونت رو بعد از کامیت بفرست",
            ["assistant_response"] = "Moved the publish after the commit.",
            ["normalized_rule"] = EventRule(new JsonObject
            {
                ["ایونت"] = "event",
                ["كاميت"] = "commit",
                ["notification"] = "event",
                ["event"] = "Event",
                ["رویداد"] = "رویداد",
            }),
        });

        Assert.True(result["submitted"]!.GetValue<bool>());
        var written = result["glossary"]!.AsArray().Select(t => t!["term"]!.GetValue<string>()).ToList();
        Assert.Equal(["ایونت", "کامیت"], written);

        await using var read = db.CreateScope();
        var glossary = await read.ServiceProvider.GetRequiredService<IGlossaryTermRepository>().ListAsync();
        Assert.Equal(
            [("ایونت", "event"), ("کامیت", "commit")],
            glossary.OrderBy(t => t.Id).Select(t => (t.Term, t.Canonical)));

        // An English synonym reaches the rule through its tags, not by rewriting English prompts.
        var rule = Assert.Single(await read.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync());
        Assert.Contains("notification", rule.Tags.Split(','));
        Assert.Equal("event", Glossary.Translate("ایونت", glossary));
    }

    [Fact]
    public async Task ReinforcingARule_AlsoKeepsTheWordsItWasSaidIn()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedEventRule(db);
        int ruleId;
        await using (var scope = db.CreateScope())
        {
            ruleId = Assert.Single(await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync()).Id;
        }

        await Judge(db, new JsonObject
        {
            ["decision"] = "ReinforceExisting",
            ["memory_type"] = "EngineeringLesson",
            ["capture_reason"] = "RepeatedMistake",
            ["confidence"] = 0.9,
            ["target_existing_rule_id"] = ruleId,
            ["dedupe_notes"] = "same rule, said in Persian",
            ["prompt"] = "باز هم ایونت قبل از کامیت رفت",
            ["assistant_response"] = "Fixed it again.",
            ["normalized_rule"] = EventRule(new JsonObject { ["ایونت"] = "event" }),
        });

        await using var read = db.CreateScope();
        var term = Assert.Single(await read.ServiceProvider.GetRequiredService<IGlossaryTermRepository>().ListAsync());
        Assert.Equal(("ایونت", "event"), (term.Term, term.Canonical));
    }

    [Fact]
    public async Task SkippedTurn_WritesNoGlossary()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await Judge(db, new JsonObject
        {
            ["decision"] = "Skip",
            ["memory_type"] = "NotMemory",
            ["capture_reason"] = "NotReusable",
            ["why_not_saved"] = "ordinary work",
            ["prompt"] = "ایونت رو درست کن",
            ["assistant_response"] = "Fixed it.",
            ["normalized_rule"] = EventRule(new JsonObject { ["ایونت"] = "event" }),
        });

        await using var read = db.CreateScope();
        Assert.Empty(await read.ServiceProvider.GetRequiredService<IGlossaryTermRepository>().ListAsync());
    }

    [Fact]
    public void ParseVerdict_AcceptsTheGlossaryAsAnArrayOfEntries()
    {
        var verdict = TurnPayload.ParseVerdict(JsonNode.Parse("""
        {
          "decision": "Capture", "memory_type": "RepositoryConvention", "capture_reason": "UserCorrection",
          "normalized_rule": { "condition": "when x", "action": "do y",
            "glossary": [{ "term": "رزرو", "english": "booking" }, { "term": "سالن", "canonical": "venue" }, { "term": "خالی" }] }
        }
        """), TextWriter.Null);

        Assert.Equal(
            [new TermMapping("رزرو", "booking"), new TermMapping("سالن", "venue")],
            verdict!.NormalizedRule!.Glossary);
    }
}
