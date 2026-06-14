using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Compression;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

public class CompressionTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task<int> Seed(
        TestDatabase db,
        string ruleText,
        RuleStatus status = RuleStatus.Active,
        double confidence = 0.5,
        string tags = "",
        string trigger = "t",
        ScopeLevel scopeLevel = ScopeLevel.Global,
        string scopeValue = "")
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var rule = await repo.AddAsync(new RecallRule
        {
            Trigger = trigger, RuleText = ruleText, Mistake = "", TechnicalContext = "",
            Tags = tags, Confidence = confidence, Status = status,
            ScopeLevel = scopeLevel, ScopeValue = scopeValue,
        });
        return rule.Id;
    }

    // ---- Canonical rule generation (pure) -------------------------------------

    [Fact]
    public void Canonicalize_PositiveDirective_PrependsAlways()
    {
        Assert.Equal("Always use parameterized SQL.",
            DeterministicCanonicalRuleGenerator.Canonicalize("Use parameterized SQL."));
    }

    [Fact]
    public void Canonicalize_DontStatement_BecomesNever()
    {
        Assert.Equal("Never concatenate SQL strings.",
            DeterministicCanonicalRuleGenerator.Canonicalize("Don't concatenate SQL strings."));
    }

    [Fact]
    public void Generate_PrefersPositiveRepresentative_AndMergesTags()
    {
        var sources = new[]
        {
            new RecallRule { RuleText = "Use parameterized SQL.", Tags = "sql", Confidence = 0.5 },
            new RecallRule { RuleText = "Avoid SQL interpolation.", Tags = "security", Confidence = 0.6 },
            new RecallRule { RuleText = "Don't concatenate SQL strings.", Tags = "sql,injection", Confidence = 0.4 },
        };

        var canonical = new DeterministicCanonicalRuleGenerator().Generate(sources);

        // The positive phrasing wins even though it isn't the most confident.
        Assert.StartsWith("Always", canonical.RuleText);
        Assert.Contains("parameterized", canonical.RuleText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sql", canonical.RuleText, StringComparison.OrdinalIgnoreCase);

        // Tags from every source are unioned.
        Assert.Contains("injection", canonical.Tags);
        Assert.Contains("security", canonical.Tags);
        Assert.Contains("sql", canonical.Tags);
    }

    // ---- Duplicate / overlap detection ----------------------------------------

    [Fact]
    public async Task Analyze_DetectsExactDuplicates()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Use dependency injection for services.");
        await Seed(db, "Use dependency injection for services.");

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMemoryCompressionService>();

        var analysis = await service.AnalyzeAsync(CompressionOptions.Default);

        var candidate = Assert.Single(analysis.Candidates);
        Assert.Equal(RuleRelationship.Duplicate, candidate.Relationship);
        Assert.Equal(2, candidate.Sources.Count);
    }

    [Fact]
    public async Task Analyze_DetectsOverlappingCorrections_OnSharedSubject()
    {
        await using var db = new TestDatabase();
        await Init(db);
        // The headline example: three differently-worded corrections about SQL.
        await Seed(db, "Use parameterized SQL.");
        await Seed(db, "Avoid SQL interpolation.");
        await Seed(db, "Don't concatenate SQL strings.");

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMemoryCompressionService>();

        var analysis = await service.AnalyzeAsync(CompressionOptions.Default);

        var candidate = Assert.Single(analysis.Candidates);
        Assert.Equal(3, candidate.Sources.Count);
        Assert.Contains("sql", candidate.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Always", candidate.CanonicalRuleText);
    }

    [Fact]
    public async Task Analyze_DoesNotGroupUnrelatedRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Use parameterized SQL.");
        await Seed(db, "Format dates as ISO 8601.");

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMemoryCompressionService>();

        var analysis = await service.AnalyzeAsync(CompressionOptions.Default);

        Assert.Empty(analysis.Candidates);
    }

    [Fact]
    public async Task Analyze_DoesNotMergeDirectConflicts()
    {
        await using var db = new TestDatabase();
        await Init(db);
        // Opposite guidance on the same subject — that's a conflict, not a merge.
        await Seed(db, "Use the repository pattern.");
        await Seed(db, "Do not use the repository pattern.");

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMemoryCompressionService>();

        var analysis = await service.AnalyzeAsync(CompressionOptions.Default);

        Assert.Empty(analysis.Candidates);
    }

    [Fact]
    public async Task Analyze_DoesNotMergeAcrossScopes()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Use parameterized SQL.", scopeLevel: ScopeLevel.Global);
        await Seed(db, "Use parameterized SQL.", scopeLevel: ScopeLevel.Repository, scopeValue: "AgentRecall");

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMemoryCompressionService>();

        var analysis = await service.AnalyzeAsync(CompressionOptions.Default);

        Assert.Empty(analysis.Candidates);
    }

    [Fact]
    public async Task Analyze_DoesNotMutate()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Use dependency injection.");
        await Seed(db, "Use dependency injection.");

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMemoryCompressionService>();
        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();

        await service.AnalyzeAsync(CompressionOptions.Default);

        var all = await rules.ListAsync();
        Assert.Equal(2, all.Count);
        Assert.All(all, r => Assert.Equal(RuleStatus.Active, r.Status));
    }

    // ---- Rule merging (apply) -------------------------------------------------

    [Fact]
    public async Task Compress_CreatesCanonical_SupersedesOriginals_AndPreservesThem()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id1 = await Seed(db, "Use parameterized SQL.", tags: "sql");
        var id2 = await Seed(db, "Avoid SQL interpolation.", tags: "security");
        var id3 = await Seed(db, "Don't concatenate SQL strings.", tags: "injection");

        CompressionResult result;
        await using (var scope = db.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IMemoryCompressionService>();
            result = await service.CompressAsync(CompressionOptions.Default);
        }

        var group = Assert.Single(result.Groups);
        Assert.StartsWith("Always", group.Canonical.RuleText);
        Assert.True(group.AuditEventId > 0);

        await using (var scope = db.CreateScope())
        {
            var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
            var all = await rules.ListAsync();

            // Originals are preserved (not deleted): 3 sources + 1 canonical.
            Assert.Equal(4, all.Count);

            foreach (var id in new[] { id1, id2, id3 })
            {
                var original = await rules.GetAsync(id);
                Assert.NotNull(original);
                Assert.Equal(RuleStatus.Superseded, original!.Status);
                Assert.Equal(group.Canonical.Id, original.SupersededById);
            }

            var canonical = await rules.GetAsync(group.Canonical.Id);
            Assert.Equal(RuleStatus.Active, canonical!.Status);
            // Confidence reflects corroboration from three sources.
            Assert.True(canonical.Confidence > 0.5);
        }
    }

    [Fact]
    public async Task Compress_RecordsAuditEventLinkingSources()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Use parameterized SQL.");
        await Seed(db, "Avoid SQL interpolation.");

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMemoryCompressionService>();
        var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();

        var result = await service.CompressAsync(CompressionOptions.Default);
        var group = Assert.Single(result.Groups);

        var all = await events.ListAsync();
        var audit = Assert.Single(all, e => e.Type == RecallEventType.RulesCompressed);
        Assert.Equal(group.Canonical.Id, audit.RuleId);
        Assert.Contains("preserved", audit.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Compress_PreservesOriginalFeedbackEvents()
    {
        await using var db = new TestDatabase();
        await Init(db);

        // Feedback creates an event + a pending rule; approve so it is compressible.
        await using (var scope = db.CreateScope())
        {
            var feedback = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
            var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();

            var a = await feedback.AddAsync(new Core.Feedback.FeedbackInput
            {
                Task = "writing SQL", Feedback = "Use parameterized SQL.",
            });
            var b = await feedback.AddAsync(new Core.Feedback.FeedbackInput
            {
                Task = "writing SQL", Feedback = "Use parameterized SQL queries.",
            });
            await lifecycle.ApproveAsync(a.Rule.Id);
            await lifecycle.ApproveAsync(b.Rule.Id);
        }

        await using (var scope = db.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IMemoryCompressionService>();
            await service.CompressAsync(CompressionOptions.Default);
        }

        await using (var scope = db.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();
            var all = await events.ListAsync();

            // The two original feedback events survive alongside the compression event.
            Assert.Equal(2, all.Count(e => e.Type == RecallEventType.MistakeObserved));
            Assert.Equal(1, all.Count(e => e.Type == RecallEventType.RulesCompressed));
        }
    }

    // ---- Statistics -----------------------------------------------------------

    [Fact]
    public async Task Compress_ReportsStatistics()
    {
        await using var db = new TestDatabase();
        await Init(db);
        // One mergeable trio + one standalone rule = 4 active rules → 2 after.
        await Seed(db, "Use parameterized SQL.");
        await Seed(db, "Avoid SQL interpolation.");
        await Seed(db, "Don't concatenate SQL strings.");
        await Seed(db, "Format dates as ISO 8601.");

        await using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMemoryCompressionService>();

        var result = await service.CompressAsync(CompressionOptions.Default);
        var stats = result.Stats;

        Assert.Equal(1, stats.CandidateCompressions);
        Assert.Equal(3, stats.RulesMerged);
        Assert.Equal(1, stats.CanonicalRulesCreated);
        Assert.Equal(4, stats.RulesBefore);
        Assert.Equal(2, stats.RulesAfter);
        // 2 of 4 rules removed = 50%.
        Assert.Equal(50.0, stats.MemoryReductionPercentage, 1);
    }

    // ---- MCP tool -------------------------------------------------------------

    [Fact]
    public void Server_RegistersCompressMemoryTool()
    {
        var names = AgentRecall.Cli.Mcp.McpServer.DefaultTools().Select(t => t.Name).ToHashSet();
        Assert.Contains("compress_memory", names);
    }

    [Fact]
    public async Task CompressMemoryTool_DryRunByDefault_DoesNotApply()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Use dependency injection.");
        await Seed(db, "Use dependency injection.");

        var tool = new AgentRecall.Cli.Mcp.Tools.CompressMemoryTool();
        await using var scope = db.CreateScope();

        var result = await tool.InvokeAsync(new JsonObject(), scope.ServiceProvider, CancellationToken.None);

        Assert.True(result["dry_run"]!.GetValue<bool>());
        Assert.Equal(1, result["statistics"]!["candidate_compressions"]!.GetValue<int>());

        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        Assert.All(await rules.ListAsync(), r => Assert.Equal(RuleStatus.Active, r.Status));
    }

    [Fact]
    public async Task CompressMemoryTool_Apply_CompressesAndReportsCanonicalId()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Use parameterized SQL.");
        await Seed(db, "Avoid SQL interpolation.");

        var tool = new AgentRecall.Cli.Mcp.Tools.CompressMemoryTool();
        await using var scope = db.CreateScope();

        var result = await tool.InvokeAsync(new JsonObject { ["dry_run"] = false },
            scope.ServiceProvider, CancellationToken.None);

        Assert.False(result["dry_run"]!.GetValue<bool>());
        var compressed = result["compressed"]!.AsArray();
        Assert.Single(compressed);
        Assert.True(compressed[0]!["canonical_rule_id"]!.GetValue<int>() > 0);
        Assert.Equal(2, compressed[0]!["superseded_ids"]!.AsArray().Count);
    }
}
