using AgentRecall.Cli.Hooks;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class HookTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task SeedMoqRule(TestDatabase db, RuleStatus status = RuleStatus.Promoted)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        await repo.AddAsync(new RecallRule
        {
            Trigger = "writing Moq tests",
            RuleText = "Use Moq argument matchers like It.IsAny<T>() consistently in tests.",
            Mistake = "Never mix raw values and matchers in one setup.",
            TechnicalContext = "", Tags = "moq,tests,testing,matchers",
            Confidence = 0.9, Status = status, ScopeLevel = ScopeLevel.Global, ScopeValue = "",
        });
    }

    private static string Payload(string prompt) =>
        $$"""{"prompt": {{System.Text.Json.JsonSerializer.Serialize(prompt)}}, "cwd": "/tmp/project"}""";

    // ---- Gate -----------------------------------------------------------------

    [Theory]
    [InlineData("Write Moq tests for OrderService", true)]
    [InlineData("implement a repository", true)]
    [InlineData("add an API endpoint", true)]
    [InlineData("what is the weather today", false)]
    [InlineData("summarize this article", false)]
    public void Gate_DetectsDevelopmentPrompts(string prompt, bool expected) =>
        Assert.Equal(expected, PromptGate.IsRelevant(prompt, PromptGate.DefaultKeywords));

    [Fact]
    public void Gate_WholeWordMatch_DoesNotFireOnLookalikes()
    {
        // "test" must not match "latest"; "api" must not match "rapid".
        Assert.False(PromptGate.IsRelevant("the latest rapid changes", ["test", "api"]));
        Assert.True(PromptGate.IsRelevant("run the test", ["test"]));
    }

    [Fact]
    public void Gate_IsConfigurable()
    {
        Assert.True(PromptGate.IsRelevant("deploy the cluster", ["deploy"]));
        Assert.False(PromptGate.IsRelevant("deploy the cluster", PromptGate.DefaultKeywords));
    }

    // ---- Formatter ------------------------------------------------------------

    [Fact]
    public void Formatter_EmptyResult_ReturnsEmpty()
    {
        var result = new ContextInjectionResult
        {
            MustFollow = [], Suggested = [], Warnings = [],
            TokensUsed = 0, TokenBudget = 0, Explanation = "",
        };

        Assert.Equal(string.Empty, HookContextFormatter.Format(result));
    }

    // ---- Hook end to end ------------------------------------------------------

    [Fact]
    public async Task Hook_DevelopmentPrompt_InjectsStructuredContext()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedMoqRule(db);

        var diagnostics = new StringWriter();
        var output = await UserPromptSubmitHook.RunAsync(
            Payload("Write Moq tests for OrderService"), db.Services, diagnostics);

        Assert.Contains("## AgentRecall Technical Context", output);
        Assert.Contains("Must Follow:", output);
        Assert.Contains("Moq argument matchers", output);
        Assert.Contains("Source Rules:", output);
    }

    // Which build answered has to reach the agent too. Hooks run the installed CLI, not the
    // working tree, so instructions can outlive the binary that must honour them; the heading
    // stamp is how a stale install becomes visible instead of silently dropping capture.
    [Fact]
    public async Task Hook_InjectedBlock_StampsTheRunningBuildAndContract()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedMoqRule(db);

        var output = await UserPromptSubmitHook.RunAsync(
            Payload("Write Moq tests for OrderService"), db.Services, new StringWriter());

        Assert.Contains($"## AgentRecall Technical Context ({Core.AgentContract.Stamp})", output, StringComparison.Ordinal);
    }

    // The retrieval id has to reach the agent: it is the handle an outcome attaches to, and the
    // agent is the only party that can report one. An injected block without it makes the
    // confidence ledger unfillable by design.
    [Fact]
    public async Task Hook_InjectedBlock_CarriesTheRetrievalIdOutcomesAttachTo()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedMoqRule(db);

        var output = await UserPromptSubmitHook.RunAsync(
            Payload("Write Moq tests for OrderService"), db.Services, new StringWriter());

        Assert.Contains("Retrieval id: ", output);

        // The id is the one actually recorded, so an outcome reported against it resolves.
        await using var scope = db.CreateScope();
        var retrievals = await scope.ServiceProvider
            .GetRequiredService<IRetrievalRecordRepository>().ListAsync();
        var recorded = Assert.Single(retrievals);
        Assert.Contains($"Retrieval id: {recorded.RetrievalId}", output);
    }

    // Nothing injected, nothing recorded: no id line to point at a retrieval that never happened.
    [Fact]
    public void Formatter_WithoutARetrieval_OmitsTheIdLine()
    {
        var result = new ContextInjectionResult
        {
            MustFollow = [], Suggested = [], Warnings = [],
            TokensUsed = 0, TokenBudget = 0, Explanation = "",
        };

        Assert.DoesNotContain("Retrieval id:", HookContextFormatter.Format(result));
    }

    [Fact]
    public async Task Hook_NonDevelopmentPrompt_InjectsNothing()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedMoqRule(db);

        var output = await UserPromptSubmitHook.RunAsync(
            Payload("what is the weather today"), db.Services, new StringWriter());

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task Hook_Disabled_InjectsNothing()
    {
        await using var db = new TestDatabase(o => o.HookEnabled = false);
        await Init(db);
        await SeedMoqRule(db);

        var output = await UserPromptSubmitHook.RunAsync(
            Payload("Write Moq tests for OrderService"), db.Services, new StringWriter());

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task Hook_MalformedPayload_DoesNotThrow_AndInjectsNothing()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var output = await UserPromptSubmitHook.RunAsync("{ not json", db.Services, new StringWriter());

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task Hook_PendingRules_IncludedByDefault_ExcludedWhenDisabled()
    {
        // Default: a relevant pending rule resurfaces, visibly marked pending —
        // otherwise it can never be recognized as a repeat and reinforced.
        await using (var db = new TestDatabase())
        {
            await Init(db);
            await SeedMoqRule(db, RuleStatus.Pending);
            var output = await UserPromptSubmitHook.RunAsync(
                Payload("Write Moq tests for OrderService"), db.Services, new StringWriter());
            Assert.Contains("AgentRecall Technical Context", output);
            Assert.Contains("(pending — not yet approved)", output);
        }

        // Explicitly disabled: pending rule is not injected.
        await using (var db = new TestDatabase(o => o.HookIncludePending = false))
        {
            await Init(db);
            await SeedMoqRule(db, RuleStatus.Pending);
            var output = await UserPromptSubmitHook.RunAsync(
                Payload("Write Moq tests for OrderService"), db.Services, new StringWriter());
            Assert.Equal(string.Empty, output);
        }
    }

    [Fact]
    public async Task Hook_PendingRules_CappedToHookPendingCap()
    {
        await using var db = new TestDatabase(o => o.HookPendingCap = 1);
        await Init(db);
        await SeedMoqRule(db, RuleStatus.Pending);
        await SeedMoqRule(db, RuleStatus.Pending);

        var output = await UserPromptSubmitHook.RunAsync(
            Payload("Write Moq tests for OrderService"), db.Services, new StringWriter());

        var occurrences = output.Split("(pending — not yet approved)").Length - 1;
        Assert.Equal(1, occurrences);
    }
}
