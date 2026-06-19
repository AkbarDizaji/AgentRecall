using System.Text.Json;
using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for <see cref="LearningReportService"/> and the <c>report</c> CLI command.
/// Every report is built from local rule/event data only and must be deterministic,
/// so timestamps are seeded explicitly and a fixed <c>AsOf</c> is used for staleness.
/// </summary>
public class LearningReportTests
{
    // A fixed reference instant so period and staleness math never depends on the wall clock.
    private static readonly DateTimeOffset June2026 = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task<int> SeedRule(
        TestDatabase db,
        DateTimeOffset createdAt,
        RuleStatus status = RuleStatus.Active,
        double confidence = 0.5,
        string tags = "",
        string ruleText = "rule",
        DateTimeOffset? lastUsedAt = null)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var rule = await repo.AddAsync(new RecallRule
        {
            Trigger = "t",
            RuleText = ruleText,
            Mistake = "",
            TechnicalContext = "",
            Tags = tags,
            Confidence = confidence,
            Status = status,
            ScopeLevel = ScopeLevel.Global,
            ScopeValue = "",
            CreatedAt = createdAt,
            LastUsedAt = lastUsedAt,
        });
        return rule.Id;
    }

    private static async Task SeedEvent(TestDatabase db, RecallEventType type, int? ruleId, DateTimeOffset createdAt)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();
        await repo.AddAsync(new RecallEvent
        {
            Type = type,
            RuleId = ruleId,
            Trigger = "seed",
            Details = "seed",
            CreatedAt = createdAt,
        });
    }

    private static async Task SeedRetrievals(TestDatabase db, int ruleId, int count, DateTimeOffset at)
    {
        for (var i = 0; i < count; i++)
        {
            await SeedEvent(db, RecallEventType.RuleApplied, ruleId, at);
        }
    }

    private static ILearningReportService Reports(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ILearningReportService>();

    [Fact]
    public async Task Monthly_AggregatesCapturesPromotionsSupersessionsRejections()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // Two captured in June, one in May (excluded).
        var r1 = await SeedRule(db, new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero));
        await SeedRule(db, new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero));
        await SeedRule(db, new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero));

        await SeedEvent(db, RecallEventType.RulePromoted, r1, new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero));
        await SeedEvent(db, RecallEventType.RuleSuperseded, r1, new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero));
        await SeedEvent(db, RecallEventType.RuleSuperseded, r1, new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)); // May, excluded
        await SeedEvent(db, RecallEventType.RuleRejected, null, new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero));

        await using var scope = db.CreateScope();
        var report = await Reports(scope).GetMonthlyReportAsync(2026, 6);

        Assert.Equal("June 2026", report.Period);
        Assert.Equal(2, report.LessonsCaptured);
        Assert.Equal(1, report.LessonsPromoted);
        Assert.Equal(1, report.LessonsSuperseded);
        Assert.Equal(1, report.LessonsRejected);
    }

    [Fact]
    public async Task Monthly_FrequentlyUsedAndMostRetrieved()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var hot = await SeedRule(db, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), ruleText: "Use It.IsAny<T>() matchers consistently.");
        var warm = await SeedRule(db, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), ruleText: "Use Result<T>.");
        var cold = await SeedRule(db, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), ruleText: "Cold rule.");

        await SeedRetrievals(db, hot, 5, June2026);
        await SeedRetrievals(db, warm, 3, June2026);
        await SeedRetrievals(db, cold, 1, June2026);

        await using var scope = db.CreateScope();
        var report = await Reports(scope).GetMonthlyReportAsync(2026, 6);

        // hot (5) and warm (3) clear the threshold of 3; cold (1) does not.
        Assert.Equal(2, report.FrequentlyUsedRules);
        Assert.NotNull(report.MostRetrievedRule);
        Assert.Equal(hot, report.MostRetrievedRule!.RuleId);
        Assert.Equal(5, report.MostRetrievedRule.RetrievalCount);
        Assert.Equal("Use It.IsAny<T>() matchers consistently.", report.MostRetrievedRule.RuleText);
    }

    [Fact]
    public async Task Monthly_AverageConfidenceAndCommonCategory()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await SeedRule(db, June2026, confidence: 0.9, tags: "testing");
        await SeedRule(db, June2026, confidence: 0.7, tags: "testing,security");
        await SeedRule(db, June2026, confidence: 0.8, tags: "security");

        await using var scope = db.CreateScope();
        var report = await Reports(scope).GetMonthlyReportAsync(2026, 6);

        Assert.Equal(0.8, report.AverageConfidence, 3);
        // "security" and "testing" both appear twice; ordinal tie-break picks "security".
        Assert.Equal("security", report.MostCommonCategory);
    }

    [Fact]
    public async Task Monthly_EmptyPeriod_IsZeroed()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedRule(db, new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));

        await using var scope = db.CreateScope();
        var report = await Reports(scope).GetMonthlyReportAsync(2026, 6);

        Assert.Equal(0, report.LessonsCaptured);
        Assert.Equal(0.0, report.AverageConfidence);
        Assert.Null(report.MostRetrievedRule);
        Assert.Null(report.MostCommonCategory);
    }

    [Fact]
    public async Task Lifecycle_CountsByStatusAndRejections()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await SeedRule(db, June2026, RuleStatus.Active);
        await SeedRule(db, June2026, RuleStatus.Active);
        await SeedRule(db, June2026, RuleStatus.Promoted);
        await SeedRule(db, June2026, RuleStatus.Superseded);
        await SeedRule(db, June2026, RuleStatus.Archived);
        await SeedEvent(db, RecallEventType.RuleRejected, null, June2026);
        await SeedEvent(db, RecallEventType.RuleRejected, null, June2026);

        await using var scope = db.CreateScope();
        var report = await Reports(scope).GetLifecycleReportAsync();

        Assert.Equal(5, report.Created);
        Assert.Equal(1, report.Promoted);
        Assert.Equal(1, report.Superseded);
        Assert.Equal(1, report.Archived);
        Assert.Equal(2, report.Rejected);
        Assert.Equal(3, report.StillActive); // 2 Active + 1 Promoted
    }

    [Fact]
    public async Task Usage_TopRetrievedRules_OrderedByCount()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var a = await SeedRule(db, June2026, ruleText: "A");
        var b = await SeedRule(db, June2026, ruleText: "B");
        await SeedRule(db, June2026, ruleText: "C"); // never retrieved

        await SeedRetrievals(db, a, 4, June2026);
        await SeedRetrievals(db, b, 9, June2026);

        await using var scope = db.CreateScope();
        var report = await Reports(scope).GetUsageReportAsync(new UsageReportOptions { AsOf = June2026 });

        Assert.Equal(2, report.TopRetrievedRules.Count);
        Assert.Equal(b, report.TopRetrievedRules[0].RuleId);
        Assert.Equal(9, report.TopRetrievedRules[0].RetrievalCount);
        Assert.Equal(a, report.TopRetrievedRules[1].RuleId);
    }

    [Fact]
    public async Task Usage_ValueScore_IsRetrievalCountTimesConfidence()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // value = 2 * 0.9 = 1.8
        var lowCountHighConf = await SeedRule(db, June2026, confidence: 0.9, ruleText: "high-conf");
        // value = 10 * 0.5 = 5.0  -> ranks first
        var highCountMidConf = await SeedRule(db, June2026, confidence: 0.5, ruleText: "high-count");

        await SeedRetrievals(db, lowCountHighConf, 2, June2026);
        await SeedRetrievals(db, highCountMidConf, 10, June2026);

        await using var scope = db.CreateScope();
        var report = await Reports(scope).GetUsageReportAsync(new UsageReportOptions { AsOf = June2026 });

        Assert.Equal(highCountMidConf, report.MostValuableLessons[0].RuleId);
        Assert.Equal(5.0, report.MostValuableLessons[0].Score, 3);
        Assert.Equal(1.8, report.MostValuableLessons[1].Score, 3);
    }

    [Fact]
    public async Task Usage_KnowledgeGrowth_IsCumulativeByMonth()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await SeedRule(db, new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));
        await SeedRule(db, new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero));
        await SeedRule(db, new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero));

        await using var scope = db.CreateScope();
        var report = await Reports(scope).GetUsageReportAsync(new UsageReportOptions { AsOf = June2026 });

        // January..March inclusive, even though February added nothing.
        Assert.Equal(3, report.KnowledgeGrowth.Count);
        Assert.Equal(2, report.KnowledgeGrowth[0].CumulativeRules);  // Jan
        Assert.Equal(2, report.KnowledgeGrowth[1].CumulativeRules);  // Feb (carried forward)
        Assert.Equal(3, report.KnowledgeGrowth[2].CumulativeRules);  // Mar
        Assert.Equal("January 2026", report.KnowledgeGrowth[0].Period);
    }

    [Fact]
    public async Task Usage_StaleRules_DetectsLongUnusedActiveRules()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var old = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // Retrieved long ago (stale).
        var stale = await SeedRule(db, old, RuleStatus.Active, confidence: 0.22, ruleText: "Use LegacyApiX",
            lastUsedAt: new DateTimeOffset(2026, 1, 23, 0, 0, 0, TimeSpan.Zero));
        // Retrieved recently (fresh).
        await SeedRule(db, old, RuleStatus.Active, ruleText: "Fresh", lastUsedAt: June2026.AddDays(-2));
        // Archived rules are never flagged.
        await SeedRule(db, old, RuleStatus.Archived, ruleText: "Archived", lastUsedAt: null);

        await using var scope = db.CreateScope();
        var report = await Reports(scope).GetUsageReportAsync(new UsageReportOptions { AsOf = June2026, StaleDays = 90 });

        var flagged = Assert.Single(report.StaleRules);
        Assert.Equal(stale, flagged.RuleId);
        Assert.Equal(143, flagged.DaysSinceLastRetrieved);
        Assert.Equal(0.22, flagged.Confidence, 3);
    }

    [Fact]
    public async Task Dna_RanksActiveConventionsAndSurfacesCategories()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var promoted = await SeedRule(db, June2026, RuleStatus.Promoted, confidence: 0.95, tags: "result", ruleText: "Use Result<T>");
        await SeedRule(db, June2026, RuleStatus.Active, confidence: 0.6, tags: "mediatr", ruleText: "Use MediatR");
        await SeedRule(db, June2026, RuleStatus.Pending, confidence: 0.9, ruleText: "Pending excluded");

        await SeedRetrievals(db, promoted, 8, June2026);

        await using var scope = db.CreateScope();
        var report = await Reports(scope).GetDnaReportAsync(top: 5);

        Assert.Equal(2, report.TopConventions.Count); // Pending is excluded
        Assert.Equal(1, report.TopConventions[0].Rank);
        Assert.Equal("Use Result<T>", report.TopConventions[0].RuleText);
        Assert.Contains(report.CoreCategories, c => c.Category == "result");
    }

    [Fact]
    public async Task Reports_AreDeterministic_AcrossRuns()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var a = await SeedRule(db, June2026, confidence: 0.8, tags: "testing", ruleText: "A");
        var b = await SeedRule(db, June2026, confidence: 0.6, tags: "security", ruleText: "B");
        await SeedRetrievals(db, a, 4, June2026);
        await SeedRetrievals(db, b, 4, June2026);

        await using var scope = db.CreateScope();
        var service = Reports(scope);
        var options = new UsageReportOptions { AsOf = June2026 };

        var first = JsonSerializer.Serialize(await service.GetUsageReportAsync(options));
        var second = JsonSerializer.Serialize(await service.GetUsageReportAsync(options));

        Assert.Equal(first, second);
        // Equal retrieval counts must tie-break on rule id, so A precedes B every time.
        var report = await service.GetUsageReportAsync(options);
        Assert.Equal(a, report.TopRetrievedRules[0].RuleId);
    }

    [Fact]
    public async Task Cli_MonthlyJson_EmitsParseableValues()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedRule(db, June2026, confidence: 0.9, tags: "testing");

        var output = new StringWriter();
        var exit = await CommandRouter.RunAsync(["report", "monthly", "--month", "2026-06", "--json"], db.Services, output);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("June 2026", doc.RootElement.GetProperty("period").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("lessonsCaptured").GetInt32());
    }

    [Fact]
    public async Task Cli_Report_InvalidMonth_Fails()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var output = new StringWriter();
        var exit = await CommandRouter.RunAsync(["report", "monthly", "--month", "nonsense"], db.Services, output);

        Assert.Equal(1, exit);
        Assert.Contains("Invalid --month", output.ToString());
    }

    /// <summary>
    /// Final validation from the spec: 12 captured lessons, 3 promoted, 2
    /// superseded, and 5 frequently retrieved rules. The monthly report must
    /// reflect those exact numbers, end-to-end through the CLI.
    /// </summary>
    [Fact]
    public async Task Validation_SampleData_ProducesExpectedMonthlyReport()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var ids = new List<int>();
        for (var i = 0; i < 12; i++)
        {
            ids.Add(await SeedRule(db, June2026.AddDays(i), confidence: 0.82, tags: "testing", ruleText: $"Lesson {i}"));
        }

        // 3 promoted, 2 superseded within June.
        for (var i = 0; i < 3; i++)
        {
            await SeedEvent(db, RecallEventType.RulePromoted, ids[i], June2026);
        }
        for (var i = 0; i < 2; i++)
        {
            await SeedEvent(db, RecallEventType.RuleSuperseded, ids[i], June2026);
        }

        // 5 rules retrieved frequently (>= threshold).
        for (var i = 0; i < 5; i++)
        {
            await SeedRetrievals(db, ids[i], 3 + i, June2026);
        }

        await using var scope = db.CreateScope();
        var report = await Reports(scope).GetMonthlyReportAsync(2026, 6);

        Assert.Equal(12, report.LessonsCaptured);
        Assert.Equal(3, report.LessonsPromoted);
        Assert.Equal(2, report.LessonsSuperseded);
        Assert.Equal(5, report.FrequentlyUsedRules);
        Assert.Equal(0.82, report.AverageConfidence, 3);
        Assert.Equal("testing", report.MostCommonCategory);
        Assert.Equal(ids[4], report.MostRetrievedRule!.RuleId); // 3+4 = 7 retrievals, the most
    }

    // ---- Retrieval recording (the source of usage data) -----------------------

    /// <summary>Seeds a rule that a "refund support" task is known to retrieve.</summary>
    private static async Task<int> SeedRetrievableRule(TestDatabase db) =>
        await SeedRule(db, June2026, RuleStatus.Promoted, confidence: 0.9,
            ruleText: "Always use a Money value object holding amount and currency together.");

    [Fact]
    public async Task RecordUsageTrue_CreatesRuleAppliedEventsAndSetsLastUsedAt()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var ruleId = await SeedRetrievableRule(db);

        await using var scope = db.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContextInjectionService>();
        var result = await context.BuildContextAsync(new ContextRequest { Task = "Add refund support", RecordUsage = true });
        Assert.Contains(result.All, r => r.Rule.Id == ruleId);

        var events = await scope.ServiceProvider.GetRequiredService<IRecallEventRepository>().ListAsync();
        var applied = events.Where(e => e.Type == RecallEventType.RuleApplied && e.RuleId == ruleId).ToList();
        Assert.Single(applied);

        var rule = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().GetAsync(ruleId);
        Assert.NotNull(rule!.LastUsedAt);
    }

    [Fact]
    public async Task RecordUsageFalse_HasNoSideEffects()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var ruleId = await SeedRetrievableRule(db);

        await using var scope = db.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContextInjectionService>();
        // Default RecordUsage is false; the result must still contain the rule.
        var result = await context.BuildContextAsync(new ContextRequest { Task = "Add refund support" });
        Assert.Contains(result.All, r => r.Rule.Id == ruleId);

        var events = await scope.ServiceProvider.GetRequiredService<IRecallEventRepository>().ListAsync();
        Assert.DoesNotContain(events, e => e.Type == RecallEventType.RuleApplied);

        var rule = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().GetAsync(ruleId);
        Assert.Null(rule!.LastUsedAt);
    }

    // ---- JSON export ----------------------------------------------------------

    [Theory]
    [InlineData("lifecycle")]
    [InlineData("usage")]
    [InlineData("dna")]
    public async Task Cli_ReportJson_IsValidJson(string sub)
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedRule(db, June2026, RuleStatus.Promoted, confidence: 0.9, tags: "testing", ruleText: "Use Result<T>");

        var output = new StringWriter();
        var exit = await CommandRouter.RunAsync(["report", sub, "--json"], db.Services, output);

        Assert.Equal(0, exit);
        // Parsing throws on malformed JSON, which would fail the test.
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task Cli_ReportJson_UsesCleanEscaping()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedRule(db, June2026, RuleStatus.Promoted, confidence: 0.9, ruleText: "Use Result<T> for failures");

        var output = new StringWriter();
        var exit = await CommandRouter.RunAsync(["report", "dna", "--json"], db.Services, output);

        Assert.Equal(0, exit);
        var json = output.ToString();
        // Angle brackets are emitted literally, not HTML-escaped to < / >.
        Assert.Contains("Result<T>", json);
        Assert.DoesNotContain("\\u003C", json);
    }

    // ---- DNA determinism ------------------------------------------------------

    [Fact]
    public async Task Dna_IsDeterministic_AcrossRuns()
    {
        await using var db = new TestDatabase();
        await Init(db);
        // Two equally-confident promoted rules force the id tie-break to decide order.
        await SeedRule(db, June2026, RuleStatus.Promoted, confidence: 0.8, tags: "a", ruleText: "Convention A");
        await SeedRule(db, June2026, RuleStatus.Promoted, confidence: 0.8, tags: "b", ruleText: "Convention B");

        await using var scope = db.CreateScope();
        var service = Reports(scope);

        var first = JsonSerializer.Serialize(await service.GetDnaReportAsync(top: 5));
        var second = JsonSerializer.Serialize(await service.GetDnaReportAsync(top: 5));

        Assert.Equal(first, second);
        var report = await service.GetDnaReportAsync(top: 5);
        Assert.Equal("Convention A", report.TopConventions[0].RuleText); // lower id wins the tie
    }

    // ---- Isolation guarantee --------------------------------------------------

    [Fact]
    public async Task TestDatabase_UsesIsolatedTempDirectory_NotUserHome()
    {
        await using var db = new TestDatabase();

        var dataDir = db.Options.DataDirectory;
        Assert.StartsWith(Path.GetTempPath(), dataDir);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.DoesNotContain(Path.Combine(home, ".agentrecall"), db.Options.DatabasePath);
    }
}
