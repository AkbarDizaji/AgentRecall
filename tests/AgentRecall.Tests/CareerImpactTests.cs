using System.Text.Json;
using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.CareerImpact;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Seeds;
using AgentRecall.Core.Summary;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Covers the opt-in career-impact pack end-to-end: the seed catalog, the deterministic
/// end-of-turn detector, the mode-gated service, the on-demand <c>career</c> commands, the
/// turn-summary pointer, and the docs. Everything runs against an isolated temp database and
/// never touches the real data directory, the network, an LLM, or embeddings. Finalize-turn
/// (stdin) integration lives in <see cref="CareerImpactStdinTests"/>.
/// </summary>
public class CareerImpactTests
{
    private static async Task<TestDatabase> NewDbAsync(Action<Core.Configuration.AgentRecallOptions>? configure = null)
    {
        var db = new TestDatabase(configure);
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        return db;
    }

    private static async Task<(int Code, string Output)> RunAsync(TestDatabase db, params string[] args)
    {
        var writer = new StringWriter();
        var code = await CommandRouter.RunAsync(args, db.Services, writer);
        return (code, writer.ToString());
    }

    private static async Task InstallAsync(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISeedPackService>()
            .InstallAsync(CareerImpactSeedPack.Name, new SeedInstallOptions());
    }

    private static async Task<T> WithScopeAsync<T>(TestDatabase db, Func<IServiceProvider, Task<T>> body)
    {
        await using var scope = db.CreateScope();
        return await body(scope.ServiceProvider);
    }

    private static readonly CareerImpactDetector Detector = new();

    private static CareerImpactAnalysis Analyze(string prompt, string response = "") =>
        Detector.Analyze(new CareerImpactInput { Prompt = prompt, Response = response });

    // ---- Catalog --------------------------------------------------------------------

    [Fact] // B. The pack has exactly ten conditional, keyed rules.
    public void Catalog_HasTenConditionalRules()
    {
        var rules = CareerImpactSeedPack.Definition.Rules;
        Assert.Equal(10, rules.Count);
        Assert.Equal(10, rules.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (var rule in rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Trigger), $"{rule.Key} has no trigger");
            Assert.False(string.IsNullOrWhiteSpace(rule.Action), $"{rule.Key} has no action");
            Assert.False(string.IsNullOrWhiteSpace(rule.AntiPattern), $"{rule.Key} has no anti-pattern");
            Assert.False(string.IsNullOrWhiteSpace(rule.Because), $"{rule.Key} has no reason");
            Assert.Contains("career-impact", rule.Tags, StringComparison.Ordinal);
        }
    }

    [Fact] // Provenance is present and disclaims copied text.
    public void Catalog_HasOriginalProvenanceNote()
    {
        var note = CareerImpactSeedPack.Definition.CopyrightNote;
        Assert.Contains("No book", note, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Seed integration (install / metadata / retrieval) --------------------------

    [Fact] // A. `seed list` shows career-impact.
    public async Task SeedList_ShowsCareerImpact()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "seed", "list");
        Assert.Equal(0, code);
        Assert.Contains("career-impact", output, StringComparison.Ordinal);
    }

    [Fact] // B. `seed show career-impact` shows all ten rule titles.
    public async Task SeedShow_ShowsTenTitles()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "seed", "show", "career-impact");
        Assert.Equal(0, code);
        foreach (var title in CareerImpactSeedPack.Definition.Rules.Select(r => r.Title))
        {
            Assert.Contains(title, output, StringComparison.Ordinal);
        }
    }

    [Fact] // C,D,E,F. Install is Active by default with seed metadata, moderate confidence, idempotent.
    public async Task Install_ActiveWithSeedMetadata_AndIdempotent()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        await InstallAsync(db); // idempotent

        var seeds = await WithScopeAsync(db, async sp =>
        {
            var rules = await sp.GetRequiredService<IRecallRuleRepository>().ListAsync();
            return rules.Where(r => r.SeedPack == CareerImpactSeedPack.Name).ToList();
        });

        Assert.Equal(10, seeds.Count);
        Assert.All(seeds, r =>
        {
            Assert.Equal(RuleStatus.Active, r.Status);
            Assert.Equal(RuleSource.BuiltInSeed, r.Source);
            Assert.Equal(CareerImpactSeedPack.Name, r.SeedPack);
            Assert.InRange(r.Confidence, 0.60, 0.70);
        });
    }

    [Fact] // G. Removing the pack archives its rules.
    public async Task Remove_ArchivesPackRules()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        await WithScopeAsync(db, sp => sp.GetRequiredService<ISeedPackService>().RemoveAsync(CareerImpactSeedPack.Name));

        var seeds = await WithScopeAsync(db, async sp =>
        {
            var rules = await sp.GetRequiredService<IRecallRuleRepository>().ListAsync();
            return rules.Where(r => r.SeedPack == CareerImpactSeedPack.Name).ToList();
        });
        Assert.All(seeds, r => Assert.Equal(RuleStatus.Archived, r.Status));
    }

    [Fact] // H. A project-specific learned rule outranks career-impact seed rules.
    public async Task LearnedRule_OutranksCareerSeed()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);

        var learnedId = await WithScopeAsync(db, async sp =>
        {
            var repo = sp.GetRequiredService<IRecallRuleRepository>();
            var learned = await repo.AddAsync(new RecallRule
            {
                Trigger = "capturing engineering impact for a promotion packet",
                RuleText = "Use our internal promotion template when recording impact.",
                Status = RuleStatus.Active,
                Confidence = 0.75,
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "myrepo",
            });
            return learned.Id;
        });

        var result = await WithScopeAsync(db, sp => sp.GetRequiredService<IContextInjectionService>()
            .BuildContextAsync(new ContextRequest
            {
                Task = "capturing engineering impact for a promotion packet with metrics and evidence",
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "myrepo",
            }));

        Assert.DoesNotContain(result.MustFollow, i => i.Rule.Source == RuleSource.BuiltInSeed);
        var order = result.All.Select(i => i.Rule).ToList();
        var learnedRank = order.FindIndex(r => r.Id == learnedId);
        var firstSeedRank = order.FindIndex(r => r.Source == RuleSource.BuiltInSeed);
        Assert.True(learnedRank >= 0 && (firstSeedRank < 0 || learnedRank < firstSeedRank));
    }

    [Fact] // I. Career-impact retrieval is capped for a non-tidy task.
    public async Task CareerRetrieval_IsCapped()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);

        var result = await WithScopeAsync(db, sp => sp.GetRequiredService<IContextInjectionService>()
            .BuildContextAsync(new ContextRequest
            {
                Task = "record the promotion impact, metrics, ADR, stakeholders, and evidence for this migration",
            }));

        Assert.True(result.All.Count(i => i.Rule.Source == RuleSource.BuiltInSeed) <= 2);
    }

    // ---- Detector: significance -----------------------------------------------------

    [Theory] // K. Significant migration / design / optimization work is detected.
    [InlineData("Plan the database migration to the new schema")]
    [InlineData("Make an architecture decision about the new service boundaries")]
    [InlineData("Optimize the checkout endpoint for lower latency")]
    public void Detector_FlagsSignificantWork(string prompt)
    {
        var analysis = Analyze(prompt);
        Assert.True(analysis.IsSignificant, prompt);
        Assert.NotEmpty(analysis.Categories);
        Assert.InRange(analysis.PromotionWorthiness, 1, 10);
    }

    [Fact] // L. Incident/postmortem work is detected and marked as incident response.
    public void Detector_FlagsIncidentWork()
    {
        var analysis = Analyze("Wrote the postmortem for last night's production outage and incident");
        Assert.True(analysis.IsSignificant);
        Assert.Contains(ImpactCategory.IncidentResponse, analysis.Categories);
    }

    [Fact] // M. An architecture decision recommends an ADR with a title.
    public void Detector_RecommendsAdrForArchitectureDecision()
    {
        var analysis = Analyze("We made an architecture decision to split the platform into services");
        Assert.True(analysis.Adr.Recommended);
        Assert.False(string.IsNullOrWhiteSpace(analysis.Adr.SuggestedTitle));
        Assert.Contains(ImpactCategory.Architecture, analysis.Categories);
    }

    [Fact] // N. Performance work suggests latency / error rate / adoption metrics.
    public void Detector_SuggestsMetricsForPerformanceWork()
    {
        var analysis = Analyze("Optimized API performance and reduced latency");
        Assert.Contains("latency", analysis.SuggestedMetrics);
        Assert.Contains("error rate", analysis.SuggestedMetrics);
        Assert.Contains("adoption", analysis.SuggestedMetrics);
    }

    [Fact] // O. Cross-team / platform work suggests stakeholders.
    public void Detector_SuggestsStakeholdersForCrossTeamWork()
    {
        var analysis = Analyze("Led a cross-team rollout of the shared platform change");
        Assert.NotEmpty(analysis.Stakeholders);
        Assert.Contains("Platform", analysis.Stakeholders);
    }

    [Fact] // P. Leadership / mentoring / design-review text detects the Leadership category.
    public void Detector_DetectsLeadership()
    {
        var analysis = Analyze("Spent the day mentoring two engineers and running a design review");
        Assert.Contains(ImpactCategory.Leadership, analysis.Categories);
    }

    [Theory] // J, AJ. Routine/trivial work is not significant.
    [InlineData("Fix a typo in the README")]
    [InlineData("Just rename this variable")]
    [InlineData("Formatting only change, whitespace")]
    [InlineData("Fixed a one-line null check")]
    public void Detector_IgnoresTrivialWork(string prompt)
    {
        var analysis = Analyze(prompt);
        Assert.False(analysis.IsSignificant, prompt);
    }

    [Fact] // AG. The detector is deterministic (no randomness, no external calls).
    public void Detector_IsDeterministic()
    {
        const string prompt = "Optimized the migration and led a cross-team rollout with new metrics";
        var a = Analyze(prompt);
        var b = Analyze(prompt);
        Assert.Equal(a.IsSignificant, b.IsSignificant);
        Assert.Equal(a.Confidence, b.Confidence);
        Assert.Equal(a.Categories, b.Categories);
        Assert.Equal(a.SuggestedMetrics, b.SuggestedMetrics);
    }

    [Fact] // The promotion note reads as natural prose, not an enum/fragment dump.
    public void PromotionNote_ReadsNaturally()
    {
        var analysis = Analyze("Plan the database migration and the architecture decision, optimizing latency");
        Assert.StartsWith("Staff-level engineering work that ", analysis.PromotionNote, StringComparison.Ordinal);
        Assert.EndsWith(".", analysis.PromotionNote, StringComparison.Ordinal);
        // No PascalCase enum name or "Category: fragment" shape leaks into the prose.
        Assert.DoesNotContain("TechnicalImpact", analysis.PromotionNote, StringComparison.Ordinal);
        Assert.DoesNotContain(": involves", analysis.PromotionNote, StringComparison.Ordinal);
        Assert.Contains(" and ", analysis.PromotionNote, StringComparison.Ordinal);
    }

    // ---- Renderer bounds ------------------------------------------------------------

    [Fact] // T. The compact summary is at most five bullets.
    public void Compact_IsAtMostFiveBullets()
    {
        var analysis = Analyze("Optimized the migration and led a cross-team rollout with metrics, dashboards, and an ADR");
        var rendered = CareerImpactRenderer.RenderCompact(analysis);
        var bullets = rendered.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal));
        Assert.True(bullets <= 5, $"expected <= 5 bullets, got {bullets}");
        Assert.Contains("career journal --last", rendered, StringComparison.Ordinal);
    }

    [Fact] // U. The detailed summary is bounded.
    public void Detailed_IsBounded()
    {
        var analysis = Analyze("Optimized the migration and led a cross-team rollout with metrics, dashboards, and an ADR");
        var rendered = CareerImpactRenderer.RenderDetailed(analysis);
        Assert.True(rendered.Length < 2000, $"detailed summary too long: {rendered.Length}");
    }

    // ---- Service: mode gating -------------------------------------------------------

    private static async Task<CareerImpactCandidate?> AnalyzeTurnAsync(TestDatabase db, string prompt, string response = "", string turnId = "t1") =>
        await WithScopeAsync(db, sp => sp.GetRequiredService<ICareerImpactService>()
            .AnalyzeTurnAsync(new CareerImpactTurnRequest { Prompt = prompt, Response = response, TurnId = turnId }));

    [Fact] // Q. Silent mode never persists a candidate.
    public async Task Mode_Silent_NeverSurfaces()
    {
        await using var db = await NewDbAsync(o => o.CareerImpactMode = "Silent");
        await InstallAsync(db);
        var candidate = await AnalyzeTurnAsync(db, "Plan the database migration to a new architecture");
        Assert.Null(candidate);
    }

    [Fact] // R. SignificantOnly persists only significant work.
    public async Task Mode_SignificantOnly_OnlySignificant()
    {
        await using var db = await NewDbAsync(o => o.CareerImpactMode = "SignificantOnly");
        await InstallAsync(db);

        Assert.Null(await AnalyzeTurnAsync(db, "Fix a typo", turnId: "trivial"));
        var significant = await AnalyzeTurnAsync(db, "Plan the database migration to a new architecture", turnId: "big");
        Assert.NotNull(significant);
        Assert.True(significant!.IsSignificant);
    }

    [Fact] // S. Always surfaces lower-confidence candidates but stays bounded.
    public async Task Mode_Always_SurfacesLowerConfidence()
    {
        await using var db = await NewDbAsync(o => o.CareerImpactMode = "Always");
        await InstallAsync(db);

        // A single weak signal is not "significant" but still carries a signal.
        var candidate = await AnalyzeTurnAsync(db, "Added a small dashboard tweak", turnId: "weak");
        Assert.NotNull(candidate);
        Assert.False(candidate!.IsSignificant);
        Assert.True(candidate.Confidence <= 0.5);
    }

    [Fact] // Detector is disabled unless the pack is installed.
    public async Task Detector_OffWhenPackNotInstalled()
    {
        await using var db = await NewDbAsync(o => o.CareerImpactMode = "Always");
        var candidate = await AnalyzeTurnAsync(db, "Plan the database migration to a new architecture");
        Assert.Null(candidate);
    }

    [Fact] // Idempotent: the same turn content persists a single candidate.
    public async Task AnalyzeTurn_IsIdempotent()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        await AnalyzeTurnAsync(db, "Plan the database migration", turnId: "t");
        await AnalyzeTurnAsync(db, "Plan the database migration", turnId: "t");

        var all = await WithScopeAsync(db, sp => sp.GetRequiredService<ICareerImpactCandidateRepository>().ListAsync());
        Assert.Single(all);
    }

    // ---- Commands -------------------------------------------------------------------

    [Fact] // AC. With no candidate, `career impact --last` reports no significant impact.
    public async Task CareerImpact_NoCandidate_ReportsNone()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        var (code, output) = await RunAsync(db, "career", "impact", "--last");
        Assert.Equal(0, code);
        Assert.Contains("no career-impact candidate recorded yet", output, StringComparison.OrdinalIgnoreCase);
        // The empty-store message must not claim it only inspected the last turn: the read is
        // whole-store, and mislabelling it that way is what makes a missing candidate look lost.
        Assert.DoesNotContain("for the last turn", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // X, Z. `career impact --last` and `--detailed` render a detected candidate.
    public async Task CareerImpact_RendersCandidate()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        await AnalyzeTurnAsync(db, "Plan the database migration and optimize latency", turnId: "t");

        var (compactCode, compact) = await RunAsync(db, "career", "impact", "--last");
        Assert.Equal(0, compactCode);
        Assert.Contains("AgentRecall Career Impact", compact, StringComparison.Ordinal);
        Assert.Contains("career journal --last", compact, StringComparison.Ordinal);

        var (detailedCode, detailed) = await RunAsync(db, "career", "impact", "--last", "--detailed");
        Assert.Equal(0, detailedCode);
        Assert.Contains("Technical impact", detailed, StringComparison.Ordinal);
    }

    [Fact] // Y, AL. JSON output is valid, deterministic, and Markdown-free.
    public async Task CareerImpact_Json_IsValidAndMarkdownFree()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        await AnalyzeTurnAsync(db, "Plan the database migration and optimize latency", turnId: "t");

        var (code, output) = await RunAsync(db, "career", "impact", "--last", "--json");
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("is_significant").GetBoolean());
        Assert.True(root.GetProperty("confidence").GetDouble() > 0);
        Assert.True(root.GetProperty("categories").GetArrayLength() > 0);
        Assert.True(root.TryGetProperty("adr", out _));

        // No rendered-notice Markdown (badge/bold) leaks into the structured output.
        Assert.DoesNotContain("🧠", output, StringComparison.Ordinal);
        Assert.DoesNotContain("**", output, StringComparison.Ordinal);
    }

    [Fact] // AA, AK. The journal is generated only on demand and carries every section.
    public async Task CareerJournal_GeneratesPromotionReadyEntry()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        await AnalyzeTurnAsync(db, "Led the cross-team database migration and optimized latency", turnId: "t");

        var (code, output) = await RunAsync(db, "career", "journal", "--last");
        Assert.Equal(0, code);
        Assert.Contains("# Career Journal Entry", output, StringComparison.Ordinal);
        foreach (var section in new[]
        {
            "Date:", "Work:", "Impact:", "Evidence:", "Metrics:", "Stakeholders:",
            "Leadership / Staff behaviors:", "ADR:", "Promotion category:",
            "Promotion-ready achievement:", "Next action:",
        })
        {
            Assert.Contains(section, output, StringComparison.Ordinal);
        }
    }

    [Fact] // AA. With no candidate, the journal command reports none (nothing generated).
    public async Task CareerJournal_NoCandidate_ReportsNone()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        var (code, output) = await RunAsync(db, "career", "journal", "--last");
        Assert.Equal(0, code);
        Assert.DoesNotContain("# Career Journal Entry", output, StringComparison.Ordinal);
        Assert.Contains("no career-impact candidate recorded yet", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // A significant candidate stays retrievable via `--last` even after a later trivial
    // turn records nothing — `--last` is the newest candidate across turns, not the last turn's.
    public async Task CareerJournal_Last_SurfacesEarlierCandidate_AfterTrivialTurn()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);

        // A significant turn records a candidate; a later trivial turn records nothing.
        await AnalyzeTurnAsync(db, "Led the cross-team database migration and optimized latency", turnId: "big");
        Assert.Null(await AnalyzeTurnAsync(db, "Fix a typo", turnId: "trivial"));

        var (code, output) = await RunAsync(db, "career", "journal", "--last");
        Assert.Equal(0, code);
        // The earlier impact is still found — a trivial latest turn never buries it.
        Assert.Contains("# Career Journal Entry", output, StringComparison.Ordinal);
        Assert.DoesNotContain("no career-impact candidate recorded yet", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // AB. `career journal --last --file` writes then appends safely (never overwrites).
    public async Task CareerJournal_File_WritesThenAppends()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        await AnalyzeTurnAsync(db, "Led the cross-team database migration", turnId: "t");

        var path = Path.Combine(Path.GetTempPath(), "agentrecall-tests", Guid.NewGuid().ToString("N"), "journal.md");
        try
        {
            var (code1, out1) = await RunAsync(db, "career", "journal", "--last", "--file", path);
            Assert.Equal(0, code1);
            Assert.Contains("Wrote career journal entry", out1, StringComparison.Ordinal);
            Assert.True(File.Exists(path));

            var (code2, out2) = await RunAsync(db, "career", "journal", "--last", "--file", path);
            Assert.Equal(0, code2);
            Assert.Contains("Appended career journal entry", out2, StringComparison.Ordinal);

            var content = await File.ReadAllTextAsync(path);
            var occurrences = content.Split("# Career Journal Entry").Length - 1;
            Assert.Equal(2, occurrences); // appended, not overwritten
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact] // `career status` reports pack/mode and the last candidate.
    public async Task CareerStatus_ReportsPackAndMode()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        var (code, output) = await RunAsync(db, "career", "status");
        Assert.Equal(0, code);
        Assert.Contains("Pack installed:  yes", output, StringComparison.Ordinal);
        Assert.Contains("SignificantOnly", output, StringComparison.Ordinal);
    }

    // ---- Turn summary pointer -------------------------------------------------------

    [Fact] // AD. The Turn Summary carries only a short career-impact pointer.
    public async Task TurnSummary_IncludesShortCareerPointer()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        await AnalyzeTurnAsync(db, "Led the cross-team database migration and optimized latency", turnId: "turn-x");

        // A used-rule activity anchors the turn so the summary is non-trivial.
        await WithScopeAsync(db, async sp =>
        {
            await sp.GetRequiredService<IAgentRecallActivityRepository>().AddAsync(new AgentRecallActivity
            {
                ActivityType = ActivityType.ContextFetched,
                TurnId = "turn-x",
                Summary = "fetched",
                Source = "test",
            });
            return true;
        });

        var summary = await WithScopeAsync(db, sp => sp.GetRequiredService<ITurnSummaryService>().BuildForTurnAsync("turn-x"));
        Assert.NotNull(summary.CareerImpact);

        var rendered = TurnSummaryRenderer.RenderDetailed(summary);
        Assert.Contains("Career Impact", rendered, StringComparison.Ordinal);
        // Only a pointer — never the full summary sections.
        Assert.DoesNotContain("Why it matters", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Evidence to collect", rendered, StringComparison.Ordinal);
    }

    // ---- Docs -----------------------------------------------------------------------

    [Fact] // AE. README documents the Career Impact Pack.
    public void Readme_DocumentsCareerImpactPack()
    {
        var readme = File.ReadAllText(FindRepoFile("README.md"));
        Assert.Contains("## Career Impact Pack", readme, StringComparison.Ordinal);
        Assert.Contains("agentrecall seed install career-impact", readme, StringComparison.Ordinal);
        Assert.Contains("agentrecall career journal --last", readme, StringComparison.Ordinal);
        Assert.Contains("CareerImpactMode", readme, StringComparison.Ordinal);
    }

    [Fact] // AF. The scaffolded CLAUDE.md documents career-impact behavior and anti-spam guidance.
    public void Scaffold_DocumentsCareerImpact()
    {
        var scaffold = File.ReadAllText(FindRepoFile(Path.Combine("src", "AgentRecall.Cli", "Devcontainer", "DevcontainerScaffolder.cs")));
        Assert.Contains("Career Impact Pack", scaffold, StringComparison.Ordinal);
        Assert.Contains("should not spam", scaffold, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not run full journal generation unless", scaffold, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate {relativePath} above {AppContext.BaseDirectory}.");
    }
}
