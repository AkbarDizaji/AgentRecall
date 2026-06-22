using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Outcomes;
using AgentRecall.Core.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for outcome-based learning: recording real outcomes moves rule confidence
/// deterministically, links to retrievals, feeds reports, and is explainable.
/// </summary>
public class OutcomeTrackingTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task<int> SeedRule(TestDatabase db, double confidence = 0.5, string ruleText = "Use parameterized queries.")
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var rule = await repo.AddAsync(new RecallRule
        {
            Trigger = "When writing SQL", RuleText = ruleText, Confidence = confidence,
            Status = RuleStatus.Active, ScopeLevel = ScopeLevel.Global,
        });
        return rule.Id;
    }

    private static async Task<OutcomeResult> Record(TestDatabase db, OutcomeRequest request)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IOutcomeTrackingService>().RecordAsync(request);
    }

    private static async Task<double> Confidence(TestDatabase db, int ruleId)
    {
        await using var scope = db.CreateScope();
        var rule = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().GetAsync(ruleId);
        return rule!.Confidence;
    }

    // A. TestsPassed raises confidence.
    [Fact]
    public async Task A_TestsPassed_IncreasesConfidence()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, 0.5);

        var result = await Record(db, new OutcomeRequest { RuleId = id, Type = OutcomeType.TestsPassed });

        Assert.True(result.Enabled);
        Assert.Equal(0.55, Assert.Single(result.Adjustments).NewConfidence, 3);
        Assert.Equal(0.55, await Confidence(db, id), 3);
    }

    // B. UserRejected lowers confidence.
    [Fact]
    public async Task B_UserRejected_DecreasesConfidence()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, 0.5);

        await Record(db, new OutcomeRequest { RuleId = id, Type = OutcomeType.UserRejected });

        Assert.Equal(0.40, await Confidence(db, id), 3);
    }

    // C. Confidence is clamped to [0, 1].
    [Fact]
    public async Task C_Confidence_IsClamped()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var high = await SeedRule(db, 0.98);
        var low = await SeedRule(db, 0.03);

        await Record(db, new OutcomeRequest { RuleId = high, Type = OutcomeType.UserAccepted }); // +0.08 -> clamp 1.0
        await Record(db, new OutcomeRequest { RuleId = low, Type = OutcomeType.CorrectionRepeated }); // -0.15 -> clamp 0.0

        Assert.Equal(1.0, await Confidence(db, high), 3);
        Assert.Equal(0.0, await Confidence(db, low), 3);
    }

    // D. A duplicate outcome event does not adjust confidence twice.
    [Fact]
    public async Task D_DuplicateOutcome_DoesNotDoubleAdjust()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, 0.5);

        await Record(db, new OutcomeRequest { RuleId = id, Type = OutcomeType.TestsPassed });
        var second = await Record(db, new OutcomeRequest { RuleId = id, Type = OutcomeType.TestsPassed });

        Assert.Empty(second.Adjustments);
        Assert.Equal(1, second.SkippedDuplicates);
        Assert.Equal(0.55, await Confidence(db, id), 3); // only one +0.05 applied
    }

    // E. Recording usage writes a retrieval record linking the injected rules.
    [Fact]
    public async Task E_RetrievalRecord_LinksInjectedRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, 0.9, "Always use parameterized queries when writing SQL.");

        string? retrievalId;
        await using (var scope = db.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IContextInjectionService>();
            var result = await context.BuildContextAsync(new ContextRequest { Task = "writing a SQL query", RecordUsage = true });
            retrievalId = result.RetrievalId;
        }

        Assert.False(string.IsNullOrEmpty(retrievalId));
        await using (var scope = db.CreateScope())
        {
            var records = await scope.ServiceProvider.GetRequiredService<IRetrievalRecordRepository>().ListAsync();
            var record = Assert.Single(records);
            Assert.Equal(retrievalId, record.RetrievalId);
            Assert.Contains(id.ToString(), record.RuleIds.Split(','));
        }
    }

    // F. Recording an outcome by retrieval-id applies to every linked rule.
    [Fact]
    public async Task F_OutcomeByRetrievalId_AppliesToAllLinkedRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var a = await SeedRule(db, 0.5, "Rule A.");
        var b = await SeedRule(db, 0.5, "Rule B.");

        await using (var scope = db.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRetrievalRecordRepository>();
            await repo.AddAsync(new RetrievalRecord { RetrievalId = "ret-123", Task = "t", RuleIds = $"{a},{b}" });
        }

        var result = await Record(db, new OutcomeRequest { RetrievalId = "ret-123", Type = OutcomeType.TestsPassed });

        Assert.Equal(2, result.Adjustments.Count);
        Assert.Equal(0.55, await Confidence(db, a), 3);
        Assert.Equal(0.55, await Confidence(db, b), 3);
    }

    // G. Recording an outcome by rule-id affects only that rule.
    [Fact]
    public async Task G_OutcomeByRuleId_AffectsOnlyThatRule()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var a = await SeedRule(db, 0.5, "Rule A.");
        var b = await SeedRule(db, 0.5, "Rule B.");

        await Record(db, new OutcomeRequest { RuleId = a, Type = OutcomeType.TestsPassed });

        Assert.Equal(0.55, await Confidence(db, a), 3);
        Assert.Equal(0.50, await Confidence(db, b), 3);
    }

    // H. The monthly report counts positive and negative outcomes.
    [Fact]
    public async Task H_MonthlyReport_IncludesOutcomes()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var a = await SeedRule(db, 0.5, "Rule A.");
        var b = await SeedRule(db, 0.5, "Rule B.");
        await Record(db, new OutcomeRequest { RuleId = a, Type = OutcomeType.TestsPassed });
        await Record(db, new OutcomeRequest { RuleId = b, Type = OutcomeType.UserRejected });

        var now = DateTimeOffset.UtcNow;
        await using var scope = db.CreateScope();
        var report = await scope.ServiceProvider.GetRequiredService<ILearningReportService>().GetMonthlyReportAsync(now.Year, now.Month);

        Assert.Equal(1, report.PositiveOutcomes);
        Assert.Equal(1, report.NegativeOutcomes);
        Assert.Contains(report.MostImprovedRules, s => s.RuleId == a);
        Assert.Contains(report.MostDegradedRules, s => s.RuleId == b);
    }

    // I. The usage report surfaces the most effective rules.
    [Fact]
    public async Task I_UsageReport_IncludesMostEffectiveRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, 0.5);
        await Record(db, new OutcomeRequest { RuleId = id, Type = OutcomeType.UserAccepted });

        await using var scope = db.CreateScope();
        var report = await scope.ServiceProvider.GetRequiredService<ILearningReportService>()
            .GetUsageReportAsync(new UsageReportOptions { AsOf = DateTimeOffset.UtcNow });

        Assert.Contains(report.MostEffectiveRules, s => s.RuleId == id && s.NetConfidenceChange > 0);
    }

    // J. `rules explain` shows the outcome history and confidence explanation.
    [Fact]
    public async Task J_RulesExplain_ShowsOutcomeHistory()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, 0.5);
        await Record(db, new OutcomeRequest { RuleId = id, Type = OutcomeType.TestsPassed });

        var output = new StringWriter();
        var exit = await CommandRouter.RunAsync(["rules", "explain", id.ToString()], db.Services, output);
        var text = output.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("TestsPassed: 1 time(s)", text, StringComparison.Ordinal);
        Assert.Contains("Net confidence change:", text, StringComparison.Ordinal);
        Assert.Contains("+0.05", text, StringComparison.Ordinal);
    }

    // K. Outcome tracking can be disabled by config.
    [Fact]
    public async Task K_OutcomeTracking_CanBeDisabled()
    {
        await using var db = new TestDatabase(o => o.OutcomeTrackingEnabled = false);
        await Init(db);
        var id = await SeedRule(db, 0.5);

        var result = await Record(db, new OutcomeRequest { RuleId = id, Type = OutcomeType.TestsPassed });

        Assert.False(result.Enabled);
        Assert.Equal(0.50, await Confidence(db, id), 3);
    }

    // L. An Unknown outcome records no confidence change.
    [Fact]
    public async Task L_UnknownOutcome_NoConfidenceChange()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, 0.5);

        var result = await Record(db, new OutcomeRequest { RuleId = id, Type = OutcomeType.Unknown });

        Assert.Equal(0.0, Assert.Single(result.Adjustments).Delta, 3);
        Assert.Equal(0.50, await Confidence(db, id), 3);
    }

    // M. CorrectionRepeated lowers confidence (the rule failed to prevent the mistake).
    [Fact]
    public async Task M_CorrectionRepeated_DecreasesConfidence()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, 0.5);

        await Record(db, new OutcomeRequest { RuleId = id, Type = OutcomeType.CorrectionRepeated });

        Assert.Equal(0.35, await Confidence(db, id), 3);
    }

    // N. The test harness is isolated to a temp directory, never the real home store.
    [Fact]
    public async Task N_TestDatabase_IsIsolated()
    {
        await using var db = new TestDatabase();

        Assert.StartsWith(Path.GetTempPath(), db.Options.DataDirectory);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.DoesNotContain(Path.Combine(home, ".agentrecall"), db.Options.DatabasePath);
    }
}
