using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class SchemaReconcilerTests
{
    /// <summary>
    /// Reproduces the field failure: a database created by an earlier version
    /// whose Rules table predates the LastUsedAt / SupersedesRuleId / Priority /
    /// Deprecated columns. After initialization the reconciler must add them so
    /// inserts that reference the current model succeed.
    /// </summary>
    [Fact]
    public async Task Initialize_AddsMissingColumns_ToLegacyRulesTable()
    {
        await using var db = new TestDatabase();
        Directory.CreateDirectory(db.Options.DataDirectory);
        WriteLegacySchema(db.Options.DatabasePath);

        await using (var scope = db.CreateScope())
        {
            var initializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
            await initializer.InitializeAsync();
        }

        await using (var scope = db.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();

            var added = await repo.AddAsync(new RecallRule
            {
                Trigger = "adding a console writeline",
                Mistake = "forgot the marker",
                RuleText = "Always add ** in console.writeline.",
                TechnicalContext = "logging",
                Tags = "log",
                Confidence = 0.8,
                ScopeLevel = ScopeLevel.Global,
                ScopeValue = string.Empty,
                Status = RuleStatus.Active,
                LastUsedAt = DateTimeOffset.UtcNow,
                Priority = 0,
                Deprecated = false,
            });

            Assert.True(added.Id > 0);
        }

        var columns = ReadColumns(db.Options.DatabasePath, "Rules");
        Assert.Contains("LastUsedAt", columns);
        Assert.Contains("SupersedesRuleId", columns);
        Assert.Contains("Priority", columns);
        Assert.Contains("Deprecated", columns);
        // The pre-existing row must survive — reconciliation is additive.
        Assert.Equal(1L, ScalarLong(db.Options.DatabasePath, "SELECT COUNT(*) FROM Rules WHERE Trigger = 'legacy';"));
    }

    /// <summary>
    /// A TurnFinalizations table created before the semantic capture judge lacks the judge
    /// decision columns; the additive reconciler must backfill them so status queries work.
    /// </summary>
    [Fact]
    public async Task Initialize_AddsJudgeDecisionColumns_ToLegacyTurnFinalizations()
    {
        await using var db = new TestDatabase();
        Directory.CreateDirectory(db.Options.DataDirectory);
        WriteLegacyTurnFinalizations(db.Options.DatabasePath);

        await using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        }

        var columns = ReadColumns(db.Options.DatabasePath, "TurnFinalizations");
        Assert.Contains("DecisionSource", columns);
        Assert.Contains("JudgeDecision", columns);
        Assert.Contains("JudgeCaptureReason", columns);
        Assert.Contains("JudgeConfidence", columns);
        // The pre-existing row survives — reconciliation is additive.
        Assert.Equal(1L, ScalarLong(db.Options.DatabasePath, "SELECT COUNT(*) FROM TurnFinalizations;"));
    }

    [Fact]
    public async Task Initialize_IsIdempotent_OnAlreadyCurrentSchema()
    {
        await using var db = new TestDatabase();

        await using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        }

        // Second run must not throw or duplicate objects.
        await using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        }

        var columns = ReadColumns(db.Options.DatabasePath, "Rules");
        Assert.Contains("LastUsedAt", columns);
    }

    private static void WriteLegacySchema(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE "Rules" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Rules" PRIMARY KEY AUTOINCREMENT,
                "Version" INTEGER NOT NULL,
                "Status" TEXT NOT NULL,
                "Trigger" TEXT NOT NULL,
                "Mistake" TEXT NOT NULL,
                "RuleText" TEXT NOT NULL,
                "TechnicalContext" TEXT NOT NULL,
                "Tags" TEXT NOT NULL,
                "Confidence" REAL NOT NULL,
                "ScopeLevel" TEXT NOT NULL,
                "ScopeValue" TEXT NOT NULL,
                "SupersededById" INTEGER NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            INSERT INTO "Rules"
                ("Version","Status","Trigger","Mistake","RuleText","TechnicalContext","Tags","Confidence","ScopeLevel","ScopeValue","CreatedAt","UpdatedAt")
            VALUES
                (1,'Active','legacy','m','r','t','','0.5','Global','','2026-06-13T00:00:00+00:00','2026-06-13T00:00:00+00:00');
            """;
        command.ExecuteNonQuery();
    }

    private static void WriteLegacyTurnFinalizations(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE "TurnFinalizations" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TurnFinalizations" PRIMARY KEY AUTOINCREMENT,
                "CreatedAt" TEXT NOT NULL,
                "Cwd" TEXT NOT NULL,
                "Source" TEXT NOT NULL,
                "CapturedRuleIds" TEXT NOT NULL,
                "SuggestedRuleIds" TEXT NOT NULL,
                "SkippedReasons" TEXT NOT NULL,
                "DuplicateRuleIds" TEXT NOT NULL,
                "ErrorSummary" TEXT NOT NULL,
                "RawHash" TEXT NOT NULL,
                "TurnId" TEXT NOT NULL,
                "Transcript" TEXT NOT NULL
            );
            INSERT INTO "TurnFinalizations"
                ("CreatedAt","Cwd","Source","CapturedRuleIds","SuggestedRuleIds","SkippedReasons","DuplicateRuleIds","ErrorSummary","RawHash","TurnId","Transcript")
            VALUES
                ('2026-06-13T00:00:00+00:00','/repo/project','stop_hook','','','','','','legacyhash','','');
            """;
        command.ExecuteNonQuery();
    }

    private static HashSet<string> ReadColumns(string path, string table)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static long ScalarLong(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
