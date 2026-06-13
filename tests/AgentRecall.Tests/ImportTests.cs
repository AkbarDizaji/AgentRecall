using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class ImportTests : IAsyncDisposable
{
    private readonly List<string> _tempFiles = [];

    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private string WriteLog(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentrecall-log-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, lines);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public async Task Import_CreatesEventForEachFailure()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var log = WriteLog(
            "Build started.",
            "Program.cs(10,5): error CS0246: type not found",
            "Program.cs(20,9): error CS0103: name does not exist",
            "Build FAILED.");

        await using var scope = db.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<ILogImportService>();

        var result = await importer.ImportAsync(LogKind.Build, log);

        // Two "error" lines plus "Build FAILED." (contains 'fail'? no — build keyword is 'error').
        Assert.Equal(2, result.FailuresFound);
        Assert.Equal(2, result.EventsCreated);

        var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();
        var all = await events.ListAsync();
        Assert.Equal(2, all.Count);
        Assert.All(all, e => Assert.Equal(RecallEventType.MistakeObserved, e.Type));
    }

    [Fact]
    public async Task Import_RepeatedFailures_IncreaseConfidence()
    {
        await using var db = new TestDatabase();
        await Init(db);

        int ruleId;
        await using (var scope = db.CreateScope())
        {
            var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            var rule = await rules.AddAsync(new RecallRule
            {
                Trigger = "CS0246", RuleText = "Reference the missing assembly.", Mistake = "",
                TechnicalContext = "", Tags = "", Confidence = 0.5, Status = RuleStatus.Active,
                ScopeLevel = ScopeLevel.Global, ScopeValue = "",
            });
            ruleId = rule.Id;
        }

        // Three matching failures: 0.5 + 3 * 0.1 = 0.8 → reinforced and auto-promoted.
        var log = WriteLog(
            "a.cs(1,1): error CS0246: type not found",
            "b.cs(2,1): error CS0246: type not found",
            "c.cs(3,1): error CS0246: type not found");

        await using (var scope = db.CreateScope())
        {
            var importer = scope.ServiceProvider.GetRequiredService<ILogImportService>();
            var result = await importer.ImportAsync(LogKind.Build, log);

            Assert.Equal(3, result.FailuresFound);
            Assert.Equal(1, result.RulesReinforced);
            Assert.Equal(1, result.RulesPromoted);
        }

        await using (var scope = db.CreateScope())
        {
            var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            var rule = await rules.GetAsync(ruleId);

            Assert.Equal(0.8, rule!.Confidence, 3);
            Assert.Equal(RuleStatus.Promoted, rule.Status);
        }
    }

    [Fact]
    public async Task Import_TestLog_DetectsFailures()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var log = WriteLog(
            "Passed!  MyTests.A",
            "Failed!  MyTests.B",
            "  Assert.Equal() Failure");

        await using var scope = db.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<ILogImportService>();

        var result = await importer.ImportAsync(LogKind.Test, log);

        // Lines containing "fail": "Failed! ..." and "... Failure".
        Assert.Equal(2, result.FailuresFound);
        Assert.Equal(2, result.EventsCreated);
    }

    [Fact]
    public async Task Import_DoesNotReinforceSupersededRules()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using (var scope = db.CreateScope())
        {
            var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            await rules.AddAsync(new RecallRule
            {
                Trigger = "CS0246", RuleText = "old", Mistake = "", TechnicalContext = "", Tags = "cs0246",
                Confidence = 0.5, Status = RuleStatus.Superseded, ScopeLevel = ScopeLevel.Global, ScopeValue = "",
            });
        }

        var log = WriteLog("x.cs(1,1): error CS0246: missing");

        await using var scope2 = db.CreateScope();
        var importer = scope2.ServiceProvider.GetRequiredService<ILogImportService>();
        var result = await importer.ImportAsync(LogKind.Build, log);

        Assert.Equal(1, result.FailuresFound);
        Assert.Equal(1, result.EventsCreated);
        Assert.Equal(0, result.RulesReinforced);
    }

    [Fact]
    public async Task Import_MissingFile_Throws()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<ILogImportService>();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => importer.ImportAsync(LogKind.Build, Path.Combine(Path.GetTempPath(), "does-not-exist-xyz.log")));
    }

    public ValueTask DisposeAsync()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch (IOException) { }
        }

        return ValueTask.CompletedTask;
    }
}
