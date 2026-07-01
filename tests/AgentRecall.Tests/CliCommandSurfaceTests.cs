using AgentRecall.Cli;
using AgentRecall.Core;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Drives the real CLI entry point (<see cref="CommandRouter.RunAsync"/>) across the
/// whole command surface: dispatch, usage/error branches, and the happy paths that
/// render each report and list. These exercise the argument parsing, validation, and
/// text/JSON rendering that unit tests of the services never reach.
///
/// None of these tests touch <see cref="Console.In"/>, so they run in parallel. The
/// handful of commands that read stdin live in <see cref="CliStdinCommandTests"/>.
/// </summary>
public class CliCommandSurfaceTests
{
    private static async Task<TestDatabase> NewDbAsync(Action<Core.Configuration.AgentRecallOptions>? configure = null)
    {
        var db = new TestDatabase(configure);
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        return db;
    }

    /// <summary>Runs a CLI command and returns the exit code and captured stdout.</summary>
    private static async Task<(int Code, string Output)> RunAsync(TestDatabase db, params string[] args)
    {
        var writer = new StringWriter();
        var code = await CommandRouter.RunAsync(args, db.Services, writer);
        return (code, writer.ToString());
    }

    private static async Task<int> SeedRuleAsync(
        TestDatabase db,
        string ruleText = "Always dispose IDisposable in a using block.",
        string trigger = "writing resource cleanup code",
        RuleStatus status = RuleStatus.Active,
        double confidence = 0.6,
        ScopeLevel scopeLevel = ScopeLevel.Global,
        string scopeValue = "",
        string tags = "cleanup,dispose")
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var rule = await repo.AddAsync(new RecallRule
        {
            Trigger = trigger,
            RuleText = ruleText,
            Mistake = "",
            TechnicalContext = "",
            Tags = tags,
            Confidence = confidence,
            Status = status,
            ScopeLevel = scopeLevel,
            ScopeValue = scopeValue,
        });
        return rule.Id;
    }

    // ---- dispatch, help, version, status, unknown -----------------------------

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task Help_PrintsUsageAndSucceeds(string arg)
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, arg);
        Assert.Equal(0, code);
        Assert.Contains("local-first memory and learning", output);
        Assert.Contains("Commands:", output);
    }

    [Fact]
    public async Task NoArguments_BehavesLikeHelp()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db);
        Assert.Equal(0, code);
        Assert.Contains("Commands:", output);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("--version")]
    [InlineData("-v")]
    public async Task Version_PrintsNameAndVersion(string arg)
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, arg);
        Assert.Equal(0, code);
        Assert.Contains(AppInfo.Name, output);
        Assert.Contains(AppInfo.Version, output);
    }

    [Fact]
    public async Task UnknownCommand_PrintsErrorAndHelp_ReturnsOne()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "does-not-exist");
        Assert.Equal(1, code);
        Assert.Contains("Unknown command: does-not-exist", output);
        Assert.Contains("Commands:", output);
    }

    [Fact]
    public async Task Init_PrintsInitializedPath()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "init");
        Assert.Equal(0, code);
        Assert.Contains("Initialized AgentRecall database at:", output);
    }

    // ---- rules ----------------------------------------------------------------

    [Fact]
    public async Task Rules_List_Empty_And_Populated_And_StatusFilter()
    {
        await using var db = await NewDbAsync();

        var (emptyCode, emptyOut) = await RunAsync(db, "rules", "list");
        Assert.Equal(0, emptyCode);
        Assert.Contains("No rules yet", emptyOut);

        await SeedRuleAsync(db, status: RuleStatus.Active);
        await SeedRuleAsync(db, ruleText: "Pending idea.", status: RuleStatus.Pending);

        var (code, output) = await RunAsync(db, "rules", "list");
        Assert.Equal(0, code);
        Assert.Contains("TRIGGER", output);
        Assert.Contains("Active", output);

        var (filterCode, filterOut) = await RunAsync(db, "rules", "list", "--status", "Pending");
        Assert.Equal(0, filterCode);
        Assert.Contains("Pending", filterOut);

        var (noneCode, noneOut) = await RunAsync(db, "rules", "list", "--status", "Archived");
        Assert.Equal(0, noneCode);
        Assert.Contains("No Archived rules.", noneOut);
    }

    [Fact]
    public async Task Rules_List_InvalidStatus_ReturnsOne()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "rules", "list", "--status", "Nonsense");
        Assert.Equal(1, code);
        Assert.Contains("Invalid --status", output);
    }

    [Fact]
    public async Task Rules_Show_Valid_Missing_And_BadId()
    {
        await using var db = await NewDbAsync();
        var id = await SeedRuleAsync(db);

        var (code, output) = await RunAsync(db, "rules", "show", id.ToString());
        Assert.Equal(0, code);
        Assert.Contains($"Rule #{id}", output);
        Assert.Contains("Confidence:", output);

        var (missCode, missOut) = await RunAsync(db, "rules", "show", "9999");
        Assert.Equal(1, missCode);
        Assert.Contains("not found", missOut);

        var (badCode, badOut) = await RunAsync(db, "rules", "show", "abc");
        Assert.Equal(1, badCode);
        Assert.Contains("Usage: agentrecall rules show", badOut);
    }

    [Fact]
    public async Task Rules_Explain_RendersProvenance()
    {
        await using var db = await NewDbAsync();
        var id = await SeedRuleAsync(db);

        var (code, output) = await RunAsync(db, "rules", "explain", id.ToString());
        Assert.Equal(0, code);
        Assert.Contains("Why this confidence:", output);
        Assert.Contains("No outcomes recorded yet", output);

        var (missCode, _) = await RunAsync(db, "rules", "explain", "9999");
        Assert.Equal(1, missCode);

        var (badCode, badOut) = await RunAsync(db, "rules", "explain", "x");
        Assert.Equal(1, badCode);
        Assert.Contains("Usage: agentrecall rules explain", badOut);
    }

    [Fact]
    public async Task Rules_Approve_Promote_Archive_Transitions()
    {
        await using var db = await NewDbAsync();
        var pending = await SeedRuleAsync(db, status: RuleStatus.Pending);

        var (approveCode, approveOut) = await RunAsync(db, "rules", "approve", pending.ToString());
        Assert.Equal(0, approveCode);
        Assert.Contains($"Rule #{pending} is now Active", approveOut);

        var (promoteCode, promoteOut) = await RunAsync(db, "rules", "promote", pending.ToString());
        Assert.Equal(0, promoteCode);
        Assert.Contains("Promoted", promoteOut);

        var (archiveCode, archiveOut) = await RunAsync(db, "rules", "archive", pending.ToString());
        Assert.Equal(0, archiveCode);
        Assert.Contains("Archived", archiveOut);
    }

    [Fact]
    public async Task Rules_Approve_BadArgs_And_MissingRule()
    {
        await using var db = await NewDbAsync();

        var (badCode, badOut) = await RunAsync(db, "rules", "promote", "notanumber");
        Assert.Equal(1, badCode);
        Assert.Contains("Usage: agentrecall rules promote", badOut);

        var (missCode, missOut) = await RunAsync(db, "rules", "approve", "4242");
        Assert.Equal(1, missCode);
        Assert.False(string.IsNullOrWhiteSpace(missOut));
    }

    [Fact]
    public async Task Rules_Supersede_And_BadArgs()
    {
        await using var db = await NewDbAsync();
        var older = await SeedRuleAsync(db, ruleText: "Old rule.");
        var newer = await SeedRuleAsync(db, ruleText: "New rule.");

        var (code, output) = await RunAsync(db, "rules", "supersede", older.ToString(), newer.ToString());
        Assert.Equal(0, code);
        Assert.Contains("replaced by rule", output);

        var (badCode, badOut) = await RunAsync(db, "rules", "supersede", "1");
        Assert.Equal(1, badCode);
        Assert.Contains("Usage: agentrecall rules supersede", badOut);
    }

    [Fact]
    public async Task Rules_Conflicts_Empty()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "rules", "conflicts");
        Assert.Equal(0, code);
        Assert.Contains("No conflicts detected.", output);
    }

    [Fact]
    public async Task Rules_Conflicts_InvalidScopeLevel()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "rules", "conflicts", "--scope-level", "Nope");
        Assert.Equal(1, code);
        Assert.Contains("Invalid --scope-level", output);
    }

    [Fact]
    public async Task Rules_UnknownSubcommand_PrintsUsage()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "rules", "frobnicate");
        Assert.Equal(1, code);
        Assert.Contains("agentrecall rules list", output);
    }

    // ---- search ---------------------------------------------------------------

    [Fact]
    public async Task Search_Results_And_NoResults()
    {
        await using var db = await NewDbAsync();
        await SeedRuleAsync(db, ruleText: "Always dispose IDisposable in a using block.", tags: "dispose,cleanup");

        var (code, output) = await RunAsync(db, "search", "dispose");
        Assert.Equal(0, code);
        Assert.Contains("result(s) for", output);

        var (noneCode, noneOut) = await RunAsync(db, "search", "zzzznomatchqueryzzz");
        Assert.Equal(0, noneCode);
        Assert.Contains("No matching rules", noneOut);
    }

    [Fact]
    public async Task Search_Usage_And_InvalidFlags()
    {
        await using var db = await NewDbAsync();

        var (usageCode, usageOut) = await RunAsync(db, "search");
        Assert.Equal(1, usageCode);
        Assert.Contains("Usage: agentrecall search", usageOut);

        var (scopeCode, scopeOut) = await RunAsync(db, "search", "q", "--scope-level", "Bogus");
        Assert.Equal(1, scopeCode);
        Assert.Contains("Invalid --scope-level", scopeOut);

        var (limitCode, limitOut) = await RunAsync(db, "search", "q", "--limit", "0");
        Assert.Equal(1, limitCode);
        Assert.Contains("Invalid --limit", limitOut);
    }

    // ---- feedback -------------------------------------------------------------

    [Fact]
    public async Task Feedback_Add_CreatesRule()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(
            db, "feedback", "add",
            "--task", "add a refund endpoint",
            "--feedback", "Always validate the refund amount is positive before persisting.");
        Assert.Equal(0, code);
        Assert.False(string.IsNullOrWhiteSpace(output));
    }

    [Fact]
    public async Task Feedback_Usage_MissingFields_And_InvalidScope()
    {
        await using var db = await NewDbAsync();

        var (usageCode, usageOut) = await RunAsync(db, "feedback");
        Assert.Equal(1, usageCode);
        Assert.Contains("Usage: agentrecall feedback add", usageOut);

        var (missCode, missOut) = await RunAsync(db, "feedback", "add", "--task", "only a task");
        Assert.Equal(1, missCode);
        Assert.Contains("Both --task and --feedback are required.", missOut);

        var (scopeCode, scopeOut) = await RunAsync(
            db, "feedback", "add", "--task", "t", "--feedback", "f", "--scope-level", "Bogus");
        Assert.Equal(1, scopeCode);
        Assert.Contains("Invalid --scope-level", scopeOut);
    }

    // ---- outcome --------------------------------------------------------------

    [Fact]
    public async Task Outcome_Record_AdjustsConfidence()
    {
        await using var db = await NewDbAsync();
        var id = await SeedRuleAsync(db);

        var (code, output) = await RunAsync(
            db, "outcome", "record", "--type", "TestsPassed", "--rule-id", id.ToString());
        Assert.Equal(0, code);
        Assert.Contains($"Rule #{id}", output);
    }

    [Fact]
    public async Task Outcome_Disabled_ReportsDisabled()
    {
        await using var db = await NewDbAsync(o => o.OutcomeTrackingEnabled = false);
        var id = await SeedRuleAsync(db);

        var (code, output) = await RunAsync(
            db, "outcome", "record", "--type", "TestsPassed", "--rule-id", id.ToString());
        Assert.Equal(0, code);
        Assert.Contains("Outcome tracking is disabled", output);
    }

    [Fact]
    public async Task Outcome_Usage_And_Validation()
    {
        await using var db = await NewDbAsync();

        var (usageCode, usageOut) = await RunAsync(db, "outcome");
        Assert.Equal(1, usageCode);
        Assert.Contains("Usage: agentrecall outcome record", usageOut);

        var (typeCode, typeOut) = await RunAsync(db, "outcome", "record", "--type", "Nonsense", "--rule-id", "1");
        Assert.Equal(1, typeCode);
        Assert.Contains("--type is required", typeOut);

        var (idCode, idOut) = await RunAsync(db, "outcome", "record", "--type", "TestsPassed", "--rule-id", "x");
        Assert.Equal(1, idCode);
        Assert.Contains("Invalid --rule-id", idOut);

        var (missCode, missOut) = await RunAsync(db, "outcome", "record", "--type", "TestsPassed");
        Assert.Equal(1, missCode);
        Assert.Contains("Provide --rule-id or --retrieval-id.", missOut);
    }

    // ---- import ---------------------------------------------------------------

    [Fact]
    public async Task Import_BuildLog_FromFile()
    {
        await using var db = await NewDbAsync();
        var path = Path.Combine(Path.GetTempPath(), $"agentrecall-buildlog-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "error CS0103: The name 'foo' does not exist in the current context\n");
        try
        {
            var (code, output) = await RunAsync(db, "import", "build-log", path);
            Assert.Equal(0, code);
            Assert.Contains("Imported Build log", output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Import_Usage_And_FileNotFound()
    {
        await using var db = await NewDbAsync();

        var (usageCode, usageOut) = await RunAsync(db, "import");
        Assert.Equal(1, usageCode);
        Assert.Contains("agentrecall import build-log", usageOut);

        var (missCode, missOut) = await RunAsync(db, "import", "test-log", "/no/such/file.log");
        Assert.Equal(1, missCode);
        Assert.Contains("Import failed:", missOut);
    }

    [Fact]
    public async Task Import_PrComments_Accepted_FromFile()
    {
        await using var db = await NewDbAsync();
        var path = Path.Combine(Path.GetTempPath(), $"agentrecall-pr-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "Always forward the custom port in login redirects.\n");
        try
        {
            var (code, output) = await RunAsync(
                db, "import", "pr-comments", path, "--accepted", "--scope-level", "Repository", "--scope-value", "skedda");
            Assert.Equal(0, code);
            Assert.Contains("Imported PR comments", output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Import_PrComments_Usage_And_InvalidScope()
    {
        await using var db = await NewDbAsync();

        var (usageCode, usageOut) = await RunAsync(db, "import", "pr-comments");
        Assert.Equal(1, usageCode);
        Assert.Contains("Usage: agentrecall import pr-comments", usageOut);

        var (scopeCode, scopeOut) = await RunAsync(db, "import", "pr-comments", "file.txt", "--scope-level", "Bogus");
        Assert.Equal(1, scopeCode);
        Assert.Contains("Invalid --scope-level", scopeOut);
    }

    // ---- inject-context -------------------------------------------------------

    [Fact]
    public async Task InjectContext_Usage_And_InvalidFlags()
    {
        await using var db = await NewDbAsync();

        var (usageCode, usageOut) = await RunAsync(db, "inject-context");
        Assert.Equal(1, usageCode);
        Assert.Contains("Usage: agentrecall inject-context", usageOut);

        var (scopeCode, scopeOut) = await RunAsync(db, "inject-context", "task", "--scope-level", "Bogus");
        Assert.Equal(1, scopeCode);
        Assert.Contains("Invalid --scope-level", scopeOut);

        var (limitCode, limitOut) = await RunAsync(db, "inject-context", "task", "--limit", "-1");
        Assert.Equal(1, limitCode);
        Assert.Contains("Invalid --limit", limitOut);
    }

    [Fact]
    public async Task InjectContext_NoRules_ReportsNone()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "inject-context", "some unrelated task");
        Assert.Equal(0, code);
        Assert.Contains("No relevant rules", output);
    }

    // ---- lessons --------------------------------------------------------------

    private static async Task<int> SeedCandidateAsync(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ILessonCandidateRepository>();
        var candidate = await repo.AddAsync(new LessonCandidate
        {
            Title = "Repeated null-check omission",
            SuggestedRule = "Guard against null before dereferencing service results.",
            Category = RuleCategory.Unknown,
            Status = LessonCandidateStatus.Suggested,
            OccurrenceCount = 3,
            Confidence = 0.7,
            FirstSeenAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            LastSeenAt = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero),
            SupportingEventIds = "1,2,3",
            NormalizedKey = "guard-null",
        });
        return candidate.Id;
    }

    [Fact]
    public async Task Lessons_List_Empty_And_Populated_And_Json()
    {
        await using var db = await NewDbAsync();

        var (emptyCode, emptyOut) = await RunAsync(db, "lessons", "list");
        Assert.Equal(0, emptyCode);
        Assert.Contains("No lesson candidates yet", emptyOut);

        await SeedCandidateAsync(db);

        var (code, output) = await RunAsync(db, "lessons", "list");
        Assert.Equal(0, code);
        Assert.Contains("TITLE", output);

        var (jsonCode, jsonOut) = await RunAsync(db, "lessons", "list", "--json");
        Assert.Equal(0, jsonCode);
        Assert.Contains("suggestedRule", jsonOut);
    }

    [Fact]
    public async Task Lessons_Show_Valid_Missing_BadArg_Json()
    {
        await using var db = await NewDbAsync();
        var id = await SeedCandidateAsync(db);

        var (code, output) = await RunAsync(db, "lessons", "show", id.ToString());
        Assert.Equal(0, code);
        Assert.Contains("Occurrences:", output);

        var (jsonCode, jsonOut) = await RunAsync(db, "lessons", "show", id.ToString(), "--json");
        Assert.Equal(0, jsonCode);
        Assert.Contains("normalizedKey", jsonOut);

        var (missCode, _) = await RunAsync(db, "lessons", "show", "9999");
        Assert.Equal(1, missCode);

        var (badCode, badOut) = await RunAsync(db, "lessons", "show", "x");
        Assert.Equal(1, badCode);
        Assert.Contains("Usage: agentrecall lessons show", badOut);
    }

    [Fact]
    public async Task Lessons_Accept_And_Reject()
    {
        await using var db = await NewDbAsync();

        var acceptId = await SeedCandidateAsync(db);
        var (acceptCode, acceptOut) = await RunAsync(db, "lessons", "accept", acceptId.ToString());
        Assert.Equal(0, acceptCode);
        Assert.Contains("Accepted lesson candidate", acceptOut);

        var rejectId = await SeedCandidateAsync(db);
        var (rejectCode, rejectOut) = await RunAsync(db, "lessons", "reject", rejectId.ToString(), "--reason", "too niche");
        Assert.Equal(0, rejectCode);
        Assert.Contains("Rejected lesson candidate", rejectOut);

        var (missCode, _) = await RunAsync(db, "lessons", "accept", "9999");
        Assert.Equal(1, missCode);
    }

    [Fact]
    public async Task Lessons_UnknownSubcommand_PrintsUsage()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "lessons", "frobnicate");
        Assert.Equal(1, code);
        Assert.Contains("agentrecall lessons mine", output);
    }

    // ---- lifecycle ------------------------------------------------------------

    private static async Task<int> SeedRecommendationAsync(TestDatabase db, int ruleId)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRuleLifecycleRecommendationRepository>();
        var rec = await repo.AddAsync(new RuleLifecycleRecommendation
        {
            RuleId = ruleId,
            RecommendationType = RecommendationType.Promote,
            Reason = "Consistently reinforced with positive outcomes.",
            Evidence = "5 retrievals, 3 TestsPassed, 0 negatives.",
            Confidence = 0.8,
            Signature = $"Promote:{ruleId}",
            Status = RecommendationStatus.Suggested,
        });
        return rec.Id;
    }

    [Fact]
    public async Task Lifecycle_List_Empty_Populated_Json()
    {
        await using var db = await NewDbAsync();

        var (emptyCode, emptyOut) = await RunAsync(db, "lifecycle", "list");
        Assert.Equal(0, emptyCode);
        Assert.Contains("No recommendations yet", emptyOut);

        var ruleId = await SeedRuleAsync(db);
        await SeedRecommendationAsync(db, ruleId);

        var (code, output) = await RunAsync(db, "lifecycle", "list");
        Assert.Equal(0, code);
        Assert.Contains("REASON", output);

        var (jsonCode, jsonOut) = await RunAsync(db, "lifecycle", "list", "--json");
        Assert.Equal(0, jsonCode);
        Assert.Contains("evidence", jsonOut);
    }

    [Fact]
    public async Task Lifecycle_Show_Apply_Reject()
    {
        await using var db = await NewDbAsync();
        var ruleId = await SeedRuleAsync(db);
        var recId = await SeedRecommendationAsync(db, ruleId);

        var (showCode, showOut) = await RunAsync(db, "lifecycle", "show", recId.ToString());
        Assert.Equal(0, showCode);
        Assert.Contains("Status:", showOut);

        var (showJsonCode, showJsonOut) = await RunAsync(db, "lifecycle", "show", recId.ToString(), "--json");
        Assert.Equal(0, showJsonCode);
        Assert.Contains("confidence", showJsonOut);

        var (applyCode, applyOut) = await RunAsync(db, "lifecycle", "apply", recId.ToString());
        Assert.Equal(0, applyCode);
        Assert.Contains($"Recommendation #{recId}", applyOut);

        var rejectId = await SeedRecommendationAsync(db, ruleId);
        var (rejectCode, rejectOut) = await RunAsync(db, "lifecycle", "reject", rejectId.ToString(), "--reason", "not now");
        Assert.Equal(0, rejectCode);
        Assert.Contains("Rejected recommendation", rejectOut);
    }

    [Fact]
    public async Task Lifecycle_Show_Missing_And_BadArg()
    {
        await using var db = await NewDbAsync();

        var (missCode, missOut) = await RunAsync(db, "lifecycle", "show", "9999");
        Assert.Equal(1, missCode);
        Assert.Contains("not found", missOut);

        var (badCode, badOut) = await RunAsync(db, "lifecycle", "show", "x");
        Assert.Equal(1, badCode);
        Assert.Contains("Usage: agentrecall lifecycle show", badOut);
    }

    [Fact]
    public async Task Lifecycle_InvalidType_And_InvalidScope()
    {
        await using var db = await NewDbAsync();

        var (typeCode, typeOut) = await RunAsync(db, "lifecycle", "suggest", "--type", "Bogus");
        Assert.Equal(1, typeCode);
        Assert.Contains("Invalid --type", typeOut);

        var (scopeCode, scopeOut) = await RunAsync(db, "lifecycle", "suggest", "--scope-level", "Bogus");
        Assert.Equal(1, scopeCode);
        Assert.Contains("Invalid --scope-level", scopeOut);
    }

    [Fact]
    public async Task Lifecycle_UnknownSubcommand_PrintsUsage()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "lifecycle", "frobnicate");
        Assert.Equal(1, code);
        Assert.Contains("agentrecall lifecycle suggest", output);
    }

    // ---- report ---------------------------------------------------------------

    [Fact]
    public async Task Report_Monthly_Text_Json_InvalidMonth()
    {
        await using var db = await NewDbAsync();

        var (code, output) = await RunAsync(db, "report", "monthly");
        Assert.Equal(0, code);
        Assert.Contains("AgentRecall Learning Report", output);

        var (jsonCode, jsonOut) = await RunAsync(db, "report", "monthly", "--month", "2026-06", "--json");
        Assert.Equal(0, jsonCode);
        Assert.Contains("period", jsonOut, StringComparison.OrdinalIgnoreCase);

        var (badCode, badOut) = await RunAsync(db, "report", "monthly", "--month", "nonsense");
        Assert.Equal(1, badCode);
        Assert.Contains("Invalid --month", badOut);
    }

    [Fact]
    public async Task Report_Lifecycle_And_Usage_And_Dna()
    {
        await using var db = await NewDbAsync();
        await SeedRuleAsync(db);

        var (lcCode, lcOut) = await RunAsync(db, "report", "lifecycle");
        Assert.Equal(0, lcCode);
        Assert.Contains("Rule Lifecycle", lcOut);

        var (usageCode, usageOut) = await RunAsync(db, "report", "usage");
        Assert.Equal(0, usageCode);
        Assert.Contains("Top Retrieved Rules", usageOut);
        Assert.Contains("Knowledge Growth", usageOut);

        var (dnaCode, dnaOut) = await RunAsync(db, "report", "dna");
        Assert.Equal(0, dnaCode);
        Assert.Contains("Project DNA", dnaOut);

        var (recCode, recOut) = await RunAsync(db, "report", "lifecycle-recommendations");
        Assert.Equal(0, recCode);
        Assert.Contains("Lifecycle Recommendations", recOut);

        var (recJsonCode, recJsonOut) = await RunAsync(db, "report", "lifecycle-recommendations", "--json");
        Assert.Equal(0, recJsonCode);
        Assert.Contains("byType", recJsonOut);
    }

    [Fact]
    public async Task Report_Usage_InvalidFlags()
    {
        await using var db = await NewDbAsync();

        var (topCode, topOut) = await RunAsync(db, "report", "usage", "--top", "0");
        Assert.Equal(1, topCode);
        Assert.Contains("Invalid --top", topOut);

        var (staleCode, staleOut) = await RunAsync(db, "report", "usage", "--stale-days", "-1");
        Assert.Equal(1, staleCode);
        Assert.Contains("Invalid --stale-days", staleOut);
    }

    [Fact]
    public async Task Report_UnknownSubcommand_PrintsUsage()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "report", "frobnicate");
        Assert.Equal(1, code);
        Assert.Contains("agentrecall report monthly", output);
    }

    // ---- dna ------------------------------------------------------------------

    [Fact]
    public async Task Dna_Text_Markdown_Json_And_Output()
    {
        await using var db = await NewDbAsync();
        await SeedRuleAsync(db);

        var (textCode, textOut) = await RunAsync(db, "dna");
        Assert.Equal(0, textCode);
        Assert.Contains("Project DNA", textOut);

        var (mdCode, mdOut) = await RunAsync(db, "dna", "--markdown");
        Assert.Equal(0, mdCode);
        Assert.Contains("# Project DNA", mdOut);

        var (jsonCode, jsonOut) = await RunAsync(db, "dna", "--json");
        Assert.Equal(0, jsonCode);
        Assert.Contains("generated_at", jsonOut);

        var path = Path.Combine(Path.GetTempPath(), $"agentrecall-dna-{Guid.NewGuid():N}.md");
        try
        {
            var (outCode, outText) = await RunAsync(db, "dna", "--markdown", "--output", path);
            Assert.Equal(0, outCode);
            Assert.Contains($"Wrote Project DNA to {path}", outText);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Dna_InvalidFlagCombinations()
    {
        await using var db = await NewDbAsync();

        var (bothCode, bothOut) = await RunAsync(db, "dna", "--json", "--markdown");
        Assert.Equal(1, bothCode);
        Assert.Contains("Choose one of --json or --markdown", bothOut);

        var (topCode, topOut) = await RunAsync(db, "dna", "--top", "0");
        Assert.Equal(1, topCode);
        Assert.Contains("Invalid --top", topOut);

        var (scopeCode, scopeOut) = await RunAsync(db, "dna", "--scope-level", "Bogus");
        Assert.Equal(1, scopeCode);
        Assert.Contains("Invalid --scope-level", scopeOut);

        var (svCode, svOut) = await RunAsync(db, "dna", "--scope-value", "skedda");
        Assert.Equal(1, svCode);
        Assert.Contains("--scope-value requires --scope-level", svOut);
    }

    // ---- activity -------------------------------------------------------------

    [Fact]
    public async Task Activity_Last_Empty_And_Json()
    {
        await using var db = await NewDbAsync();

        var (code, output) = await RunAsync(db, "activity", "last");
        Assert.Equal(0, code);
        Assert.Contains("no activity recorded yet", output);

        var (jsonCode, jsonOut) = await RunAsync(db, "activity", "last", "--json");
        Assert.Equal(0, jsonCode);
        Assert.Contains("null", jsonOut);
    }

    [Fact]
    public async Task Activity_List_InvalidLimit_And_UnknownSub()
    {
        await using var db = await NewDbAsync();

        var (limitCode, limitOut) = await RunAsync(db, "activity", "list", "--limit", "0");
        Assert.Equal(1, limitCode);
        Assert.Contains("Invalid --limit", limitOut);

        var (subCode, subOut) = await RunAsync(db, "activity", "frobnicate");
        Assert.Equal(1, subCode);
        Assert.Contains("agentrecall activity last", subOut);
    }

    // ---- eval -----------------------------------------------------------------

    [Fact]
    public async Task Eval_Usage_And_BadDataset()
    {
        await using var db = await NewDbAsync();

        var (usageCode, usageOut) = await RunAsync(db, "eval");
        Assert.Equal(1, usageCode);
        Assert.Contains("Usage: agentrecall eval retrieval", usageOut);

        var (badCode, badOut) = await RunAsync(db, "eval", "retrieval", "--dataset", "/no/such/dataset.json");
        Assert.Equal(1, badCode);
        Assert.Contains("Failed to load evaluation dataset", badOut);
    }

    // ---- devcontainer ---------------------------------------------------------

    [Fact]
    public async Task Devcontainer_Usage_And_Init()
    {
        await using var db = await NewDbAsync();

        var (usageCode, usageOut) = await RunAsync(db, "devcontainer");
        Assert.Equal(1, usageCode);
        Assert.Contains("Usage: agentrecall devcontainer init", usageOut);

        var dir = Path.Combine(Path.GetTempPath(), $"agentrecall-dc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        try
        {
            var (code, output) = await RunAsync(db, "devcontainer", "init", dir);
            Assert.Equal(0, code);
            Assert.False(string.IsNullOrWhiteSpace(output));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
