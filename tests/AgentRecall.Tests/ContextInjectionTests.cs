using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class ContextInjectionTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task<int> Seed(
        TestDatabase db,
        string ruleText,
        string tags = "",
        string trigger = "",
        double confidence = 0.6,
        RuleStatus status = RuleStatus.Active,
        ScopeLevel scopeLevel = ScopeLevel.Global,
        string scopeValue = "",
        string mistake = "",
        bool alwaysApply = false)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var rule = await repo.AddAsync(new RecallRule
        {
            Trigger = trigger, RuleText = ruleText, Mistake = mistake, TechnicalContext = "",
            Tags = tags, Confidence = confidence, Status = status,
            ScopeLevel = scopeLevel, ScopeValue = scopeValue, AlwaysApply = alwaysApply,
        });
        return rule.Id;
    }

    private static async Task<ContextInjectionResult> Build(TestDatabase db, ContextRequest request)
    {
        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IContextInjectionService>();
        return await service.BuildContextAsync(request);
    }

    // ---- Retrieval recording (batched writes) ---------------------------------

    [Fact]
    public async Task RecordUsage_BatchWritesEventsAndBumpsLastUsed()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await Seed(db, "Always use parameterized SQL queries.", tags: "sql,security", confidence: 0.9);
        await Seed(db, "Validate tenant scope before returning data.", tags: "security,tenant", confidence: 0.9);

        ContextInjectionResult result;
        await using (var scope = db.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IContextInjectionService>();
            result = await service.BuildContextAsync(new ContextRequest
            {
                Task = "fix a SQL security issue with tenant validation",
                RecordUsage = true,
            });
        }

        Assert.NotEmpty(result.All);
        Assert.NotNull(result.RetrievalId);

        await using (var scope = db.CreateScope())
        {
            var events = await scope.ServiceProvider.GetRequiredService<IRecallEventRepository>().ListAsync();
            var applied = events.Where(e => e.Type == RecallEventType.RuleApplied).ToList();
            Assert.Equal(result.All.Count(), applied.Count);

            // Every injected rule had its LastUsedAt stamped by the batched update.
            var rules = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync();
            foreach (var injected in result.All)
            {
                Assert.NotNull(rules.Single(r => r.Id == injected.Rule.Id).LastUsedAt);
            }
        }
    }

    // ---- Semantic similarity --------------------------------------------------

    [Fact]
    public async Task RetrievesRuleBySemanticRelation_NotJustKeyword()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // The Money rule never mentions "refund", yet the task is about refunds.
        var moneyId = await Seed(db,
            "Always use a Money value object holding amount and currency together.",
            tags: "money,domain-modeling,value-object", confidence: 0.9, status: RuleStatus.Promoted);
        await Seed(db, "Format dates as ISO 8601.", tags: "dates", confidence: 0.9, status: RuleStatus.Promoted);

        var result = await Build(db, new ContextRequest { Task = "Add refund support" });

        // Retrieved by meaning, and ranked top.
        Assert.Contains(result.All, r => r.Rule.Id == moneyId);
        Assert.Equal(moneyId, result.All.First().Rule.Id);

        var money = result.All.First(r => r.Rule.Id == moneyId);
        Assert.Equal(RuleImportance.MustFollow, money.Importance);
        Assert.Contains("money", money.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refund", money.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DoesNotRetrieveUnrelatedRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Format dates as ISO 8601.", tags: "dates");

        var result = await Build(db, new ContextRequest { Task = "Add refund support" });

        Assert.Empty(result.All);
    }

    // ---- Domain matches (files / changed entities) ----------------------------

    [Fact]
    public async Task MatchesOnChangedEntitiesAndFileNames()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var moneyId = await Seed(db,
            "Use a Money value object for amounts and currency.",
            tags: "money", confidence: 0.7);

        // Task text is generic; the signal comes from the changed code.
        var result = await Build(db, new ContextRequest
        {
            Task = "Update the service",
            FileNames = ["PaymentService.cs"],
            ChangedEntities = ["InvoiceLineItem", "Refund"],
        });

        var money = Assert.Single(result.All, r => r.Rule.Id == moneyId);
        Assert.Contains("money", money.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirectEntityTokenMatchIsExplained()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await Seed(db, "Validate currency codes against ISO 4217.", tags: "currency");

        var result = await Build(db, new ContextRequest
        {
            Task = "tweak code",
            ChangedEntities = ["CurrencyConverter"],
        });

        var rule = Assert.Single(result.All, r => r.Rule.Id == id);
        Assert.Contains("currency", rule.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Task-type matches ----------------------------------------------------

    [Fact]
    public async Task TaskTypeBoostsAlignedRule()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var securityId = await Seed(db,
            "Sanitize all user input before use.", tags: "security,validation", confidence: 0.6);
        var styleId = await Seed(db,
            "Sanitize file names for readability.", tags: "style", confidence: 0.6);

        var result = await Build(db, new ContextRequest
        {
            Task = "sanitize incoming data",
            TaskType = TaskType.Security,
        });

        // Both mention "sanitize", but the security task lifts the security rule.
        var security = result.All.First(r => r.Rule.Id == securityId);
        var style = result.All.First(r => r.Rule.Id == styleId);
        Assert.True(security.Score > style.Score);
        Assert.Contains("Security", security.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Ranking quality ------------------------------------------------------

    [Fact]
    public async Task RanksByUsefulness_ConfidenceAndScope()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // All three are about SQL; usefulness should order them.
        var promoted = await Seed(db, "Use parameterized SQL queries.", tags: "sql",
            confidence: 0.95, status: RuleStatus.Promoted);
        var project = await Seed(db, "Use the project SQL helper for queries.", tags: "sql",
            confidence: 0.6, scopeLevel: ScopeLevel.Repository, scopeValue: "AgentRecall");
        var weak = await Seed(db, "Consider indexing SQL queries.", tags: "sql", confidence: 0.3);

        var result = await Build(db, new ContextRequest
        {
            Task = "write SQL queries",
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "AgentRecall",
        });

        var order = result.All.Select(r => r.Rule.Id).ToList();
        // The weak, low-confidence global rule ranks last.
        Assert.Equal(weak, order.Last());
        Assert.Contains(promoted, order);
        Assert.Contains(project, order);

        // Scores are monotonically non-increasing across the combined ranking.
        var scores = result.All.Select(r => r.Score).ToList();
        Assert.Equal(scores.OrderByDescending(s => s), scores);
    }

    [Fact]
    public async Task ExcludeRuleIds_DropsRulesFromSelectionAndUsageRecording()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var kept = await Seed(db, "Always use parameterized SQL queries.", tags: "sql", confidence: 0.9);
        var excluded = await Seed(db, "Validate tenant scope before SQL.", tags: "sql", confidence: 0.9);

        ContextInjectionResult result;
        await using (var scope = db.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IContextInjectionService>();
            result = await service.BuildContextAsync(new ContextRequest
            {
                Task = "write a SQL query",
                ExcludeRuleIds = new HashSet<int> { excluded },
                RecordUsage = true,
            });
        }

        var ids = result.All.Select(r => r.Rule.Id).ToList();
        Assert.Contains(kept, ids);
        Assert.DoesNotContain(excluded, ids);

        // The excluded rule is not recorded as used either (no RuleApplied event for it).
        await using var readScope = db.CreateScope();
        var events = await readScope.ServiceProvider.GetRequiredService<IRecallEventRepository>().ListAsync();
        Assert.DoesNotContain(events, e => e.RuleId == excluded && e.Type == RecallEventType.RuleApplied);
        Assert.Contains(events, e => e.RuleId == kept && e.Type == RecallEventType.RuleApplied);
    }

    [Fact]
    public async Task FileScopedRule_RewardedWhenChangedFileIsWithinScope()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // A rule bound to a specific file, stored with a repository-relative path.
        var fileRule = await Seed(db, "Prefer composition in this file.", tags: "design",
            scopeLevel: ScopeLevel.File, scopeValue: "src/Auth/LoginService.cs");

        // The request carries the repository as its ScopeValue (as the hooks send it) and the
        // absolute path of the file being changed.
        var result = await Build(db, new ContextRequest
        {
            Task = "apply a design rule",
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "AgentRecall",
            FileNames = ["/Users/dev/AgentRecall/src/Auth/LoginService.cs"],
        });

        var injected = result.All.SingleOrDefault(r => r.Rule.Id == fileRule);
        Assert.NotNull(injected);
        // The scope bonus fired: the rule is treated as project-specific, not scored 0.0.
        Assert.Contains(injected!.MatchReasons, m => m.Contains("contains a changed file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectoryScopedRule_RewardedWhenChangedFileIsUnderDirectory()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var dirRule = await Seed(db, "Handlers in this folder must be idempotent.", tags: "design",
            scopeLevel: ScopeLevel.Directory, scopeValue: "src/Handlers");

        var result = await Build(db, new ContextRequest
        {
            Task = "apply a design rule",
            ScopeValue = "AgentRecall",
            FileNames = ["/Users/dev/AgentRecall/src/Handlers/RefundHandler.cs"],
        });

        var injected = result.All.SingleOrDefault(r => r.Rule.Id == dirRule);
        Assert.NotNull(injected);
        Assert.Contains(injected!.MatchReasons, m => m.Contains("contains a changed file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileScopedRule_NotRewardedWhenChangedFileIsElsewhere()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var fileRule = await Seed(db, "Prefer composition in this file.", tags: "design",
            scopeLevel: ScopeLevel.File, scopeValue: "src/Auth/LoginService.cs");

        var result = await Build(db, new ContextRequest
        {
            Task = "apply a design rule",
            ScopeValue = "AgentRecall",
            FileNames = ["/Users/dev/AgentRecall/src/Billing/InvoiceService.cs"],
        });

        // It may still surface on the keyword match, but never with the scope reason.
        var injected = result.All.SingleOrDefault(r => r.Rule.Id == fileRule);
        if (injected is not null)
        {
            Assert.DoesNotContain(injected.MatchReasons, m => m.Contains("contains a changed file", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ProhibitionRuleBecomesWarning()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Never concatenate SQL strings.", tags: "sql,security", confidence: 0.9);

        var result = await Build(db, new ContextRequest { Task = "build a SQL query" });

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("Never", warning.Rule.RuleText);
    }

    // ---- Policy integration & token budget ------------------------------------

    [Fact]
    public async Task ConflictingRulesArePrunedByPolicyEngine()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Use the repository pattern.", tags: "repository", confidence: 0.5);
        await Seed(db, "Do not use the repository pattern.", tags: "repository",
            confidence: 0.9, status: RuleStatus.Promoted);

        var result = await Build(db, new ContextRequest { Task = "structure repository data access" });

        // The conflict is resolved: only the winner survives.
        Assert.Single(result.All);
        Assert.Contains("set aside", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RespectsTokenBudget()
    {
        await using var db = new TestDatabase();
        await Init(db);
        for (var i = 0; i < 10; i++)
        {
            await Seed(db, $"Use parameterized SQL queries, variation {i} with extra descriptive text.",
                tags: "sql", confidence: 0.7);
        }

        var result = await Build(db, new ContextRequest
        {
            Task = "write SQL queries",
            TokenBudget = 60,
        });

        Assert.True(result.TokensUsed <= 60);
        Assert.NotEmpty(result.All); // at least the top rule fits
        Assert.Contains("budget", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // ---- MCP tool -------------------------------------------------------------

    // ---- Round-trip: store → promote → inject --------------------------------

    [Fact]
    public async Task RoundTrip_MoqRule_IsReturnedAsMustFollow()
    {
        await using var db = new TestDatabase();
        await Init(db);

        int ruleId;
        await using (var scope = db.CreateScope())
        {
            // Store a rule about Moq matcher usage via the real capture path.
            var feedback = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
            var captured = await feedback.AddAsync(new Core.Feedback.FeedbackInput
            {
                Task = "writing Moq unit tests",
                Feedback = "Always use Moq argument matchers like It.IsAny<T>() consistently in Moq tests.",
                Tags = "moq,testing,matchers",
            });
            Assert.NotNull(captured.Rule);
            ruleId = captured.Rule.Id;

            // Approve/promote it.
            var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();
            await lifecycle.PromoteAsync(ruleId);
        }

        await using (var scope = db.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IContextInjectionService>();

            // A new, overlapping task.
            var result = await service.BuildContextAsync(new ContextRequest { Task = "write Moq tests for service X" });

            Assert.Contains(result.MustFollow, r => r.Rule.Id == ruleId);
            Assert.Contains(ruleId, ContextProjection.SourceRuleIds(result));
        }
    }

    [Fact]
    public async Task PendingRules_ExcludedByDefault_IncludedOnRequest()
    {
        await using var db = new TestDatabase();
        await Init(db);
        // Captured as Pending explicitly.
        await Seed(db, "Use Moq argument matchers in tests.", tags: "moq", status: RuleStatus.Pending);

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IContextInjectionService>();

        var withoutPending = await service.BuildContextAsync(new ContextRequest { Task = "write Moq tests" });
        Assert.Empty(withoutPending.All);

        var withPending = await service.BuildContextAsync(new ContextRequest { Task = "write Moq tests", IncludePending = true });
        Assert.NotEmpty(withPending.All);
        // Pending rules are never elevated to must-follow.
        Assert.Empty(withPending.MustFollow);
    }

    [Fact]
    public async Task PendingCap_KeepsOnlyHighestScoringPendingRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        // Identical trigger/text/tags so relevance is equal — confidence is the only
        // variable driving score, isolating the cap's "highest-scoring" behavior.
        var lowConfidenceId = await Seed(db, "Use Moq argument matchers in tests.",
            tags: "moq", confidence: 0.55, status: RuleStatus.Pending);
        var highConfidenceId = await Seed(db, "Use Moq argument matchers in tests.",
            tags: "moq", confidence: 0.75, status: RuleStatus.Pending);

        var result = await Build(db, new ContextRequest
        {
            Task = "write Moq tests",
            IncludePending = true,
            PendingCap = 1,
        });

        var pendingIds = result.All.Where(r => r.Rule.Status == RuleStatus.Pending).Select(r => r.Rule.Id).ToList();
        Assert.Single(pendingIds);
        Assert.Equal(highConfidenceId, pendingIds[0]);
        Assert.NotEqual(lowConfidenceId, pendingIds[0]);
    }

    [Fact]
    public async Task PendingCap_Null_LeavesAllQualifyingPendingRulesInPlace()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Use Moq argument matchers in tests.", tags: "moq", status: RuleStatus.Pending);
        await Seed(db, "Prefer It.IsAny<T>() over raw values in Moq setups.", tags: "moq", status: RuleStatus.Pending);

        var result = await Build(db, new ContextRequest { Task = "write Moq tests", IncludePending = true });

        Assert.Equal(2, result.All.Count(r => r.Rule.Status == RuleStatus.Pending));
    }

    [Fact]
    public async Task Cli_InjectContext_PrintsAgentOptimizedSections()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await Seed(db, "Always use Moq argument matchers like It.IsAny<T>() in Moq tests.",
            tags: "moq,testing", confidence: 0.95, status: RuleStatus.Promoted);

        var output = new StringWriter();
        var code = await AgentRecall.Cli.CommandRouter.RunAsync(
            ["inject-context", "write Moq tests for service X"], db.Services, output);

        Assert.Equal(0, code);
        var text = output.ToString();
        // Rules are rendered as conditional blocks under a "Must Follow" section.
        Assert.Contains("Must Follow:", text);
        Assert.Contains("Do:", text);
        Assert.Contains($"#{id}", text);
        Assert.Contains("Source rule IDs:", text);
    }

    [Fact]
    public void Server_RegistersInjectContextTool()
    {
        var names = AgentRecall.Cli.Mcp.McpServer.DefaultTools().Select(t => t.Name).ToHashSet();
        Assert.Contains("inject_context", names);
    }

    [Fact]
    public async Task InjectContextTool_ReturnsBucketedRulesWithExplanations()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Always use a Money value object holding amount and currency.",
            tags: "money", confidence: 0.9, status: RuleStatus.Promoted);

        var tool = new AgentRecall.Cli.Mcp.Tools.InjectContextTool();
        await using var scope = db.CreateScope();

        var args = new JsonObject
        {
            ["task"] = "add refund support",
            ["changed_entities"] = new JsonArray { "Refund" },
        };
        var result = await tool.InvokeAsync(args, scope.ServiceProvider, CancellationToken.None);

        var mustFollow = result["must_follow"]!.AsArray();
        Assert.Single(mustFollow);
        Assert.False(string.IsNullOrWhiteSpace(mustFollow[0]!["explanation"]!.GetValue<string>()));
        Assert.NotNull(mustFollow[0]!["match_reasons"]);
        Assert.True(result["tokens_used"]!.GetValue<int>() > 0);
    }

    // ---- Always-apply (standing) rules ----------------------------------------

    [Fact] // A standing rule reaches the model even when it shares no keyword with the task.
    public async Task AlwaysApplyRule_IsInjectedDespiteZeroRelevance()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // No token here overlaps with the date-parsing task below (non-prohibition phrasing, so it
        // buckets as must-follow rather than a warning).
        var standingId = await Seed(db, "Keep comments minimal and purposeful.",
            tags: "style", confidence: 0.7, alwaysApply: true);
        // A non-standing rule with the same (zero) relevance must NOT be injected.
        await Seed(db, "Prefer composition over inheritance.", tags: "design", confidence: 0.7);

        var result = await Build(db, new ContextRequest { Task = "add a function to parse ISO dates" });

        var injectedIds = result.All.Select(i => i.Rule.Id).ToList();
        Assert.Contains(standingId, injectedIds);
        Assert.Single(injectedIds); // only the standing rule cleared, the irrelevant one did not
        Assert.Contains(result.MustFollow, i => i.Rule.Id == standingId);
        Assert.Contains(result.MustFollow, i => i.Explanation.Contains("standing rule", StringComparison.Ordinal));
    }

    [Fact] // The standing band is capped so it cannot flood the context.
    public async Task AlwaysApplyBand_IsCappedAtFive()
    {
        await using var db = new TestDatabase();
        await Init(db);

        for (var i = 0; i < 8; i++)
        {
            await Seed(db, $"Standing constraint number {i} about zzzptmatter.",
                confidence: 0.5 + i * 0.05, alwaysApply: true);
        }

        var result = await Build(db, new ContextRequest { Task = "add a function to parse ISO dates" });

        // At most five standing rules are delivered as the reserved band; the rest fall back to
        // relevance gating and (sharing no keywords) drop out.
        Assert.Equal(5, result.All.Count());
    }
}
