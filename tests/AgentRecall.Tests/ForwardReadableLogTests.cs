using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// One database, two builds. AgentRecall's log tables are written by whichever build ran last
/// and read by whichever runs next, and a machine routinely has both — an installed global tool
/// and a working copy. A newer build records an activity type, an event type or an outcome type
/// the older one has never heard of, and the older one must still be able to read its own log:
/// these are the tables `agentrecall activity`, `turn-summary` and `capture-status` read to
/// answer "what did AgentRecall do?", so failing the query there is failing the one question the
/// user is told to trust. The unknown row degrades to a fallback member and keeps its summary
/// text; it never takes the whole listing down with it.
/// </summary>
public class ForwardReadableLogTests
{
    private static async Task<TestDatabase> NewDbAsync()
    {
        var db = new TestDatabase();
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        return db;
    }

    [Fact]
    public async Task Activity_WrittenByANewerBuild_ReadsBackAsUnknownInsteadOfThrowing()
    {
        await using var db = await NewDbAsync();

        await using (var scope = db.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AgentRecallDbContext>();
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO Activities (ActivityType, NoticeLevel, CreatedAt, Source, Summary, TurnId)
                VALUES ('SomethingThisBuildHasNeverHeardOf', 'Deafening', '2026-09-02T10:00:00+00:00',
                        'stop_hook', 'A newer AgentRecall recorded this.', 'turn-1')
                """);
        }

        await using var reader = db.CreateScope();
        var activities = await reader.ServiceProvider
            .GetRequiredService<IAgentRecallActivityRepository>().ListAsync();

        var row = Assert.Single(activities);
        Assert.Equal(ActivityType.Unknown, row.ActivityType);
        Assert.Equal(NoticeLevel.Silent, row.NoticeLevel);

        // The row still says what happened — that text was written for a human, not for the enum.
        Assert.Equal("A newer AgentRecall recorded this.", row.Summary);
    }

    [Fact]
    public async Task RuleEvent_WrittenByANewerBuild_ReadsBackAsUnknown()
    {
        await using var db = await NewDbAsync();

        await using (var scope = db.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AgentRecallDbContext>();
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO Events (Type, RuleId, Trigger, Details, CreatedAt)
                VALUES ('RuleTeleported', 1, 'retrieval', 'From a later build.', '2026-09-02T10:00:00+00:00')
                """);
        }

        await using var reader = db.CreateScope();
        var events = await reader.ServiceProvider
            .GetRequiredService<IRecallEventRepository>().ListAsync();

        Assert.Equal(RecallEventType.Unknown, Assert.Single(events).Type);
    }

    [Fact]
    public async Task Outcome_WrittenByANewerBuild_ReadsBackAsUnknown()
    {
        await using var db = await NewDbAsync();

        await using (var scope = db.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AgentRecallDbContext>();
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO Outcomes (RuleId, RetrievalId, Type, ConfidenceDelta, Reason, CreatedAt)
                VALUES (1, 'abc123', 'ShippedToProduction', 0.05, 'From a later build.',
                        '2026-09-02T10:00:00+00:00')
                """);
        }

        await using var reader = db.CreateScope();
        var outcomes = await reader.ServiceProvider
            .GetRequiredService<IRuleOutcomeRepository>().ListAsync();

        Assert.Equal(OutcomeType.Unknown, Assert.Single(outcomes).Type);
    }

    // A known name still round-trips by name, so tolerance did not turn into looseness.
    [Fact]
    public async Task KnownActivityType_StillRoundTripsByName()
    {
        await using var db = await NewDbAsync();

        await using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAgentRecallActivityRepository>().AddAsync(
                new AgentRecallActivity
                {
                    ActivityType = ActivityType.TurnFinalized,
                    NoticeLevel = NoticeLevel.Normal,
                    Source = "stop_hook",
                    Summary = "Finalized.",
                });
        }

        await using var reader = db.CreateScope();
        var context = reader.ServiceProvider.GetRequiredService<AgentRecallDbContext>();
        var stored = await context.Database
            .SqlQueryRaw<string>("SELECT ActivityType AS Value FROM Activities")
            .SingleAsync();

        Assert.Equal(nameof(ActivityType.TurnFinalized), stored);
    }
}
