using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Covers the SQLite concurrency posture: AgentRecall runs as several short-lived processes
/// (the MCP server plus one per hook fire) against one local database, and the PreToolUse hook
/// writes on every file-mutating tool call — so racing writers are routine. These assert the
/// command timeout (wait-and-retry instead of an immediate SQLITE_BUSY) and WAL are configured,
/// and that concurrent writers all succeed instead of one silently dropping its write.
/// </summary>
public class PersistenceConcurrencyTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    [Fact]
    public async Task DbContext_HasABusyCommandTimeout()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentRecallDbContext>();

        // A configured (non-null, positive) command timeout is what makes Microsoft.Data.Sqlite
        // retry on a locked database rather than fail immediately.
        var timeout = context.Database.GetCommandTimeout();
        Assert.NotNull(timeout);
        Assert.True(timeout > 0, $"expected a positive command timeout, got {timeout}.");
    }

    [Fact]
    public async Task Database_UsesWalJournalMode()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentRecallDbContext>();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)await command.ExecuteScalarAsync();

        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public async Task ConcurrentWriters_AllSucceed_WithoutDatabaseLocked()
    {
        await using var db = new TestDatabase();
        await Init(db);

        const int writers = 24;
        var tasks = Enumerable.Range(0, writers).Select(async i =>
        {
            await using var scope = db.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IAgentRecallActivityRepository>();
            await repo.AddAsync(new AgentRecallActivity
            {
                ActivityType = ActivityType.ContextFetched,
                Summary = $"writer {i}",
                Source = "test",
            });
        });

        // Without the busy timeout these racing writers throw "database is locked"; with it they
        // serialise and every write lands.
        var exception = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
        Assert.Null(exception);

        await using var readScope = db.CreateScope();
        var recorded = await readScope.ServiceProvider
            .GetRequiredService<IAgentRecallActivityRepository>().ListRecentAsync(1000);
        Assert.Equal(writers, recorded.Count);
    }
}
