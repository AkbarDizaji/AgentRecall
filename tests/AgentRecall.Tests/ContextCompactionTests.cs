using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// What the injected block costs, and why. Every rule injected is context the agent pays for on
/// that turn, so the budget has to measure the text that is actually sent, a suggestion should not
/// cost what a must-follow rule costs, and a rule this chat has already read should not be sent
/// again in full. All three were measured on a real database before they were changed: a block
/// rendered at 1.5x its own estimate, and 92% of all rule injections were repeats.
/// </summary>
public class ContextCompactionTests
{
    private static async Task<TestDatabase> NewDbAsync()
    {
        var db = new TestDatabase();
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        return db;
    }

    private static async Task<int> SeedAsync(
        TestDatabase db,
        string trigger,
        string action,
        string avoid = "Avoid the other thing entirely, in every case, without exception.",
        string because = "Because the alternative has failed repeatedly and costs more to undo than to prevent.",
        double confidence = 0.9)
    {
        await using var scope = db.CreateScope();
        var rule = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().AddAsync(new RecallRule
        {
            Trigger = trigger,
            RuleText = action,
            Mistake = avoid,
            TechnicalContext = because,
            Tags = "sql,queries",
            Confidence = confidence,
            Status = RuleStatus.Active,
            ScopeLevel = ScopeLevel.Global,
            ScopeValue = "",
        });
        return rule.Id;
    }

    private static async Task<ContextInjectionResult> BuildAsync(TestDatabase db, ContextRequest request)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IContextInjectionService>()
            .BuildContextAsync(request);
    }

    private static ContextRequest Request(string? session = null) => new()
    {
        Task = "write SQL queries for the report",
        RecordUsage = session is not null,
        SessionId = session,
    };

    // The budget is only worth having if it counts the string that is injected. It used to count
    // the stored fields plus a match explanation that is never rendered, while omitting the
    // rationale that always is.
    [Fact]
    public async Task EstimatedTokens_MatchTheRenderedBlock()
    {
        await using var db = await NewDbAsync();
        await SeedAsync(db, "when writing SQL queries", "Use parameterized SQL queries everywhere.");

        var result = await BuildAsync(db, Request());

        var injected = Assert.Single(result.All);
        var rendered = ConditionalRuleFormatter.Format(
            injected.Rule, indent: 2, includeSource: true, detail: injected.Detail);

        // Within the bullet-and-newline allowance the estimate adds for the section around it.
        Assert.InRange(injected.EstimatedTokens, rendered.Length / 4, (rendered.Length / 4) + 4);
    }

    [Fact]
    public async Task ASuggestion_CostsLessThanAMustFollowRule()
    {
        await using var db = await NewDbAsync();
        await SeedAsync(db, "when writing SQL queries", "Use parameterized SQL queries everywhere.", confidence: 0.95);
        await SeedAsync(db, "when writing SQL queries in a report", "Prefer a read replica for report queries.", confidence: 0.3);

        var result = await BuildAsync(db, Request());

        var mustFollow = Assert.Single(result.MustFollow);
        var suggested = Assert.Single(result.Suggested);

        Assert.Equal(RuleDetail.Full, mustFollow.Detail);
        Assert.Equal(RuleDetail.Compact, suggested.Detail);
        Assert.True(
            suggested.EstimatedTokens < mustFollow.EstimatedTokens,
            "a compact suggestion should cost less than a fully rendered must-follow rule.");
    }

    [Fact]
    public void CompactRendering_KeepsTheConditionAndAction_DropsTheRest()
    {
        var rule = new RecallRule
        {
            Id = 7,
            Trigger = "when writing SQL queries",
            RuleText = "Use parameterized SQL queries everywhere.",
            Mistake = "Never concatenate user input into SQL.",
            TechnicalContext = "String concatenation is how injection gets in.",
            Tags = "", ScopeValue = "",
        };

        var compact = ConditionalRuleFormatter.Format(rule, detail: RuleDetail.Compact);

        Assert.Contains("Do: Use parameterized SQL queries everywhere.", compact, StringComparison.Ordinal);
        Assert.Contains("Source: #7", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("Avoid:", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("Because:", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void ReminderRendering_IsOneLineNamingTheRule()
    {
        var rule = new RecallRule
        {
            Id = 18,
            Trigger = "when running dotnet build in this repo",
            RuleText = "Build with -p:NuGetAudit=false.",
            Mistake = "Never build at the solution root.",
            TechnicalContext = "The private feed is unreachable from most sandboxes.",
            Tags = "", ScopeValue = "",
        };

        var reminder = ConditionalRuleFormatter.Format(rule, detail: RuleDetail.Reminder);

        Assert.Single(reminder.Split('\n'));
        Assert.StartsWith("#18 still applies", reminder, StringComparison.Ordinal);
        Assert.Contains("Build with -p:NuGetAudit=false.", reminder, StringComparison.Ordinal);
    }

    // The saving this exists for: a rule the same chat has already been shown is reminded, not
    // restated. 92% of injections on the measured database were repeats of exactly this kind.
    [Fact]
    public async Task ARuleTheChatHasAlreadyRead_IsRemindedRatherThanRestated()
    {
        await using var db = await NewDbAsync();
        await SeedAsync(db, "when writing SQL queries", "Use parameterized SQL queries everywhere.");

        var first = await BuildAsync(db, Request(session: "chat-1"));
        var second = await BuildAsync(db, Request(session: "chat-1"));

        Assert.Equal(RuleDetail.Full, Assert.Single(first.All).Detail);

        var repeat = Assert.Single(second.All);
        Assert.Equal(RuleDetail.Reminder, repeat.Detail);
        Assert.True(
            repeat.EstimatedTokens < Assert.Single(first.All).EstimatedTokens,
            "a reminder should cost less than the full rule.");
    }

    // A different chat has read nothing, so it gets the rule in full.
    [Fact]
    public async Task AnotherChat_StillReceivesTheRuleInFull()
    {
        await using var db = await NewDbAsync();
        await SeedAsync(db, "when writing SQL queries", "Use parameterized SQL queries everywhere.");

        await BuildAsync(db, Request(session: "chat-1"));
        var other = await BuildAsync(db, Request(session: "chat-2"));

        Assert.Equal(RuleDetail.Full, Assert.Single(other.All).Detail);
    }

    // A long chat gets compacted, so what was read early may be out of view: the rule is restated
    // in full periodically rather than reminded forever.
    [Fact]
    public async Task ARepeatedRule_IsRestatedInFullPeriodically()
    {
        await using var db = await NewDbAsync();
        await SeedAsync(db, "when writing SQL queries", "Use parameterized SQL queries everywhere.");

        var details = new List<RuleDetail>();
        for (var turn = 0; turn < 8; turn++)
        {
            var result = await BuildAsync(db, Request(session: "chat-1"));
            details.Add(Assert.Single(result.All).Detail);
        }

        Assert.Equal(RuleDetail.Full, details[0]);
        Assert.Contains(RuleDetail.Reminder, details);
        Assert.True(
            details.Skip(1).Any(d => d == RuleDetail.Full),
            "a rule repeated across a long chat should be restated in full at some point.");
    }

    // Without a session there is no shared history to lean on, so nothing is assumed read.
    [Fact]
    public async Task WithoutASession_EveryRuleIsRenderedInFull()
    {
        await using var db = await NewDbAsync();
        await SeedAsync(db, "when writing SQL queries", "Use parameterized SQL queries everywhere.");

        await BuildAsync(db, Request());
        var second = await BuildAsync(db, Request());

        Assert.Equal(RuleDetail.Full, Assert.Single(second.All).Detail);
    }

    // One lesson under two triggers is one lesson: it is rendered once, and the copy is not
    // recorded as injected either, so no outcome can be claimed for a rule nobody saw.
    [Fact]
    public async Task TwoRulesWithTheSameAction_AreInjectedOnce()
    {
        await using var db = await NewDbAsync();
        const string Action = "Use parameterized SQL queries everywhere.";
        await SeedAsync(db, "when writing SQL queries", Action, confidence: 0.9);
        await SeedAsync(db, "when writing a SQL report query", Action, confidence: 0.8);

        var result = await BuildAsync(db, Request());

        Assert.Single(result.All);
    }

    // Trimming used to cut mid-word and spend the whole allowance on a fragment.
    [Fact]
    public async Task ALongLine_IsTrimmedAtABoundaryNotMidWord()
    {
        await using var db = await NewDbAsync();
        await SeedAsync(
            db,
            "when writing SQL queries",
            "Use parameterized SQL queries everywhere. " + string.Join(' ', Enumerable.Repeat("elaboration", 40)));

        var block = HookContextFormatter.Format(await BuildAsync(db, Request()));
        var trimmed = block.Split('\n').Single(l => l.TrimEnd().EndsWith('…'));

        Assert.EndsWith(" …", trimmed.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("elaborati…", trimmed, StringComparison.Ordinal);
    }

    // The id list at the end repeated what every rule already carries on its own Source line.
    [Fact]
    public async Task TheBlock_CarriesEachRulesSourceWithoutARepeatedIdList()
    {
        await using var db = await NewDbAsync();
        await SeedAsync(db, "when writing SQL queries", "Use parameterized SQL queries everywhere.");

        var block = HookContextFormatter.Format(await BuildAsync(db, Request()));

        Assert.Contains("Source: #", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Source Rules:", block, StringComparison.Ordinal);
    }
}
