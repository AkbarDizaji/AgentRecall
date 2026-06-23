using System.Text.Json;
using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Mining;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for Lesson Mining: scanning historical signals (corrections, PR comments,
/// failures, rejected patterns) and proposing deduplicated, deterministic lesson
/// candidates that a human reviews — distinct from reports, compression, and
/// single-message classification.
/// </summary>
public class LessonMiningTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task SeedFeedback(TestDatabase db, string feedback, string trigger = "a task")
    {
        await using var scope = db.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();
        await events.AddAsync(new RecallEvent
        {
            Type = RecallEventType.MistakeObserved,
            Trigger = trigger,
            Details = $"Feedback: {feedback}",
        });
    }

    private static async Task SeedFailure(TestDatabase db, string message)
    {
        await using var scope = db.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();
        await events.AddAsync(new RecallEvent
        {
            Type = RecallEventType.MistakeObserved,
            Trigger = "test failure",
            Details = message,
        });
    }

    private static async Task SeedActiveRule(TestDatabase db, string ruleText)
    {
        await using var scope = db.CreateScope();
        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        await rules.AddAsync(new RecallRule
        {
            Trigger = "When writing tests", RuleText = ruleText, Confidence = 0.9,
            Status = RuleStatus.Active, ScopeLevel = ScopeLevel.Global,
        });
    }

    private static async Task<MiningResult> Mine(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ILessonMiningService>().MineAsync();
    }

    // A. Repeated corrections generate a candidate.
    [Fact]
    public async Task A_RepeatedCorrections_GenerateCandidate()
    {
        await using var db = new TestDatabase();
        await Init(db);
        for (var i = 0; i < 3; i++) await SeedFeedback(db, "Use It.IsAny<T>() for matchers.");

        var result = await Mine(db);

        var candidate = Assert.Single(result.Suggested);
        Assert.Equal(3, candidate.OccurrenceCount);
        Assert.Equal(0.60, candidate.Confidence, 3);
    }

    // B. Repeated PR review comments generate a candidate.
    [Fact]
    public async Task B_RepeatedPrComments_GenerateCandidate()
    {
        await using var db = new TestDatabase();
        await Init(db);
        for (var i = 0; i < 3; i++)
            await SeedFeedback(db, "Wrap external HTTP calls in a retry policy.", trigger: "reviewing PaymentClient.cs");

        var result = await Mine(db);

        Assert.Single(result.Suggested);
        Assert.Equal(3, result.Suggested[0].OccurrenceCount);
    }

    // C. Repeated failures generate a candidate.
    [Fact]
    public async Task C_RepeatedFailures_GenerateCandidate()
    {
        await using var db = new TestDatabase();
        await Init(db);
        for (var i = 0; i < 4; i++) await SeedFailure(db, "NullReferenceException in OrderService.Process");

        var result = await Mine(db);

        var candidate = Assert.Single(result.Suggested);
        Assert.Equal(4, candidate.OccurrenceCount);
        Assert.Equal(0.70, candidate.Confidence, 3);
    }

    // D. Similar wording clusters into a single candidate with occurrence count 3.
    [Fact]
    public async Task D_SimilarWording_ClustersTogether()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedFeedback(db, "Use It.IsAny<T>()");
        await SeedFeedback(db, "Please use It.IsAny<T>()");
        await SeedFeedback(db, "Should use It.IsAny<T>() here");

        var result = await Mine(db);

        var candidate = Assert.Single(result.Suggested);
        Assert.Equal(3, candidate.OccurrenceCount);
    }

    // E. An active rule suppresses a duplicate candidate.
    [Fact]
    public async Task E_ActiveRule_SuppressesCandidate()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedActiveRule(db, "Use It.IsAny<T>()");
        for (var i = 0; i < 3; i++) await SeedFeedback(db, "Use It.IsAny<T>()");

        var result = await Mine(db);

        Assert.Empty(result.Suggested);
        Assert.True(result.SuppressedByRule >= 1);
    }

    // F. A rejected candidate suppresses future duplicate proposals.
    [Fact]
    public async Task F_RejectedCandidate_SuppressesFutureProposals()
    {
        await using var db = new TestDatabase();
        await Init(db);
        for (var i = 0; i < 3; i++) await SeedFeedback(db, "Use It.IsAny<T>()");

        var first = await Mine(db);
        var id = Assert.Single(first.Suggested).Id;

        await using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ILessonMiningService>().RejectAsync(id, "not useful");
        }

        var second = await Mine(db);

        Assert.Empty(second.Suggested);
        Assert.True(second.SuppressedByRejection >= 1);

        await using (var scope = db.CreateScope())
        {
            var all = await scope.ServiceProvider.GetRequiredService<ILessonCandidateRepository>().ListAsync();
            Assert.Single(all); // not duplicated
            Assert.Equal(LessonCandidateStatus.Rejected, all[0].Status);
        }
    }

    // G. Accepting a candidate creates a RecallRule.
    [Fact]
    public async Task G_AcceptingCandidate_CreatesRecallRule()
    {
        await using var db = new TestDatabase();
        await Init(db);
        for (var i = 0; i < 3; i++) await SeedFeedback(db, "Use It.IsAny<T>() for matchers.");
        var id = Assert.Single((await Mine(db)).Suggested).Id;

        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ILessonMiningService>().AcceptAsync(id);

        var rules = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync();
        var rule = Assert.Single(rules);
        Assert.Contains("It.IsAny<T>()", rule.RuleText, StringComparison.Ordinal);
        Assert.Equal(RuleStatus.Active, rule.Status);
    }

    // H. An accepted candidate's status becomes Accepted.
    [Fact]
    public async Task H_AcceptedCandidate_StatusIsAccepted()
    {
        await using var db = new TestDatabase();
        await Init(db);
        for (var i = 0; i < 3; i++) await SeedFeedback(db, "Use It.IsAny<T>() for matchers.");
        var id = Assert.Single((await Mine(db)).Suggested).Id;

        await using var scope = db.CreateScope();
        var accepted = await scope.ServiceProvider.GetRequiredService<ILessonMiningService>().AcceptAsync(id);

        Assert.NotNull(accepted);
        Assert.Equal(LessonCandidateStatus.Accepted, accepted!.Status);
    }

    // I. A rejected candidate's status becomes Rejected and stores the reason.
    [Fact]
    public async Task I_RejectedCandidate_StatusAndReason()
    {
        await using var db = new TestDatabase();
        await Init(db);
        for (var i = 0; i < 3; i++) await SeedFeedback(db, "Use It.IsAny<T>() for matchers.");
        var id = Assert.Single((await Mine(db)).Suggested).Id;

        await using var scope = db.CreateScope();
        var rejected = await scope.ServiceProvider.GetRequiredService<ILessonMiningService>()
            .RejectAsync(id, "too specific");

        Assert.NotNull(rejected);
        Assert.Equal(LessonCandidateStatus.Rejected, rejected!.Status);
        Assert.Equal("too specific", rejected.RejectedReason);
    }

    // J. Confidence scoring is deterministic.
    [Fact]
    public void J_ConfidenceScoring_IsDeterministic()
    {
        Assert.Equal(0.60, LessonMiningService.ConfidenceFor(3), 3);
        Assert.Equal(0.70, LessonMiningService.ConfidenceFor(4), 3);
        Assert.Equal(0.80, LessonMiningService.ConfidenceFor(5), 3);
        Assert.Equal(1.00, LessonMiningService.ConfidenceFor(10), 3);
        Assert.Equal(1.00, LessonMiningService.ConfidenceFor(25), 3);
        Assert.Equal(LessonMiningService.ConfidenceFor(7), LessonMiningService.ConfidenceFor(7), 6);
    }

    // K. JSON output is valid.
    [Fact]
    public async Task K_MineJson_IsValid()
    {
        await using var db = new TestDatabase();
        await Init(db);
        for (var i = 0; i < 3; i++) await SeedFeedback(db, "Use It.IsAny<T>() for matchers.");

        var output = new StringWriter();
        var exit = await CommandRouter.RunAsync(["lessons", "mine", "--json"], db.Services, output);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var first = doc.RootElement[0];
        Assert.Equal(3, first.GetProperty("occurrenceCount").GetInt32());
        Assert.Equal(JsonValueKind.Array, first.GetProperty("supportingEventIds").ValueKind);
        Assert.Equal(3, first.GetProperty("supportingEventIds").GetArrayLength());
    }

    // L. Mining does not mutate existing RecallRules.
    [Fact]
    public async Task L_Mining_DoesNotMutateRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedActiveRule(db, "Use parameterized SQL queries.");
        for (var i = 0; i < 3; i++) await SeedFeedback(db, "Use It.IsAny<T>() for matchers.");

        RecallRule before;
        await using (var scope = db.CreateScope())
            before = (await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync()).Single();

        await Mine(db);

        await using (var scope = db.CreateScope())
        {
            var after = (await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync()).Single();
            Assert.Equal(before.Confidence, after.Confidence, 6);
            Assert.Equal(before.Status, after.Status);
            Assert.Equal(before.RuleText, after.RuleText);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        }
    }

    // M. Mining is idempotent: running twice does not duplicate candidates.
    [Fact]
    public async Task M_Mining_IsIdempotent()
    {
        await using var db = new TestDatabase();
        await Init(db);
        for (var i = 0; i < 3; i++) await SeedFeedback(db, "Use It.IsAny<T>() for matchers.");

        await Mine(db);
        await Mine(db);

        await using var scope = db.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<ILessonCandidateRepository>().ListAsync();
        Assert.Single(all);
    }

    // N. The harness is isolated to a temp directory, never the real home store.
    [Fact]
    public async Task N_TestDatabase_IsIsolated()
    {
        await using var db = new TestDatabase();

        Assert.StartsWith(Path.GetTempPath(), db.Options.DataDirectory);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.DoesNotContain(Path.Combine(home, ".agentrecall"), db.Options.DatabasePath);
    }
}
