using System.Text.Json;
using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Dna;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for <see cref="ProjectDnaService"/> and the <c>dna</c> CLI command.
/// Project DNA is built from local data only and must be deterministic, so
/// timestamps and the <c>AsOf</c> reference instant are seeded explicitly. Every
/// test runs against an isolated temp database (never <c>~/.agentrecall</c>).
/// </summary>
public class ProjectDnaTests
{
    // A fixed reference instant so recency/staleness never depends on the wall clock.
    private static readonly DateTimeOffset June2026 = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task<int> SeedRule(
        TestDatabase db,
        string ruleText,
        RuleStatus status = RuleStatus.Active,
        RuleCategory category = RuleCategory.Unknown,
        double confidence = 0.6,
        string tags = "",
        string mistake = "",
        ScopeLevel scopeLevel = ScopeLevel.Global,
        string scopeValue = "",
        DateTimeOffset? createdAt = null,
        DateTimeOffset? lastUsedAt = null)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var rule = await repo.AddAsync(new RecallRule
        {
            Trigger = "t",
            RuleText = ruleText,
            Mistake = mistake,
            TechnicalContext = "",
            Tags = tags,
            Category = category,
            Confidence = confidence,
            Status = status,
            ScopeLevel = scopeLevel,
            ScopeValue = scopeValue,
            CreatedAt = createdAt ?? June2026,
            LastUsedAt = lastUsedAt,
        });
        return rule.Id;
    }

    private static async Task SeedRetrievals(TestDatabase db, int ruleId, int count)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();
        for (var i = 0; i < count; i++)
        {
            await repo.AddAsync(new RecallEvent
            {
                Type = RecallEventType.RuleApplied,
                RuleId = ruleId,
                Trigger = "seed",
                Details = "seed",
                CreatedAt = June2026,
            });
        }
    }

    private static async Task SeedOutcome(TestDatabase db, int ruleId, OutcomeType type, double delta)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRuleOutcomeRepository>();
        await repo.AddAsync(new RuleOutcome
        {
            RuleId = ruleId,
            Type = type,
            ConfidenceDelta = delta,
            Reason = "seed",
            CreatedAt = June2026,
        });
    }

    private static async Task SeedCandidate(TestDatabase db, string title, int occurrences = 3, double confidence = 0.7)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ILessonCandidateRepository>();
        await repo.AddAsync(new LessonCandidate
        {
            Title = title,
            SuggestedRule = title,
            Category = RuleCategory.EngineeringLesson,
            Status = LessonCandidateStatus.Suggested,
            OccurrenceCount = occurrences,
            Confidence = confidence,
            FirstSeenAt = June2026,
            LastSeenAt = June2026,
            NormalizedKey = title.ToLowerInvariant(),
            CreatedAt = June2026,
            UpdatedAt = June2026,
        });
    }

    private static IProjectDnaService Service(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IProjectDnaService>();

    private static ProjectDnaOptions Options(int top = 5, ScopeLevel? scopeLevel = null, string? scopeValue = null) =>
        new() { AsOf = June2026, Top = top, ScopeLevel = scopeLevel, ScopeValue = scopeValue };

    private static DnaSection Section(ProjectDnaReport report, string key) =>
        report.Sections.Single(s => s.Key == key);

    private static IEnumerable<int> AllRuleIds(ProjectDnaReport report) =>
        report.Sections.SelectMany(s => s.Items).SelectMany(i => i.RuleIds);

    // A. DNA includes high-confidence promoted rules.
    [Fact]
    public async Task A_Includes_HighConfidencePromotedRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, "Prefer Result<T> for recoverable domain failures.",
            status: RuleStatus.Promoted, category: RuleCategory.EngineeringLesson, confidence: 0.95);

        await using var scope = db.CreateScope();
        var report = await Service(scope).GenerateAsync(Options());

        Assert.Contains(id, AllRuleIds(report));
    }

    // B. DNA ranks promoted rules above active rules when otherwise similar.
    [Fact]
    public async Task B_RanksPromotedAboveActive_WhenOtherwiseSimilar()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var active = await SeedRule(db, "Active principle", status: RuleStatus.Active,
            category: RuleCategory.EngineeringLesson, confidence: 0.8);
        var promoted = await SeedRule(db, "Promoted principle", status: RuleStatus.Promoted,
            category: RuleCategory.EngineeringLesson, confidence: 0.8);

        await using var scope = db.CreateScope();
        var report = await Service(scope).GenerateAsync(Options());

        var principles = Section(report, SectionKeys.CorePrinciples).Items;
        var promotedIndex = principles.ToList().FindIndex(i => i.RuleIds.Contains(promoted));
        var activeIndex = principles.ToList().FindIndex(i => i.RuleIds.Contains(active));
        Assert.True(promotedIndex >= 0 && activeIndex >= 0);
        Assert.True(promotedIndex < activeIndex);
    }

    // C. DNA groups testing rules under Testing Patterns.
    [Fact]
    public async Task C_GroupsTestingRules_UnderTestingPatterns()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, "When writing Moq tests, use It.IsAny<T>() for irrelevant arguments.",
            tags: "testing,moq", category: RuleCategory.RepositoryConvention, scopeLevel: ScopeLevel.Repository, scopeValue: "demo");

        await using var scope = db.CreateScope();
        var report = await Service(scope).GenerateAsync(Options());

        Assert.Contains(Section(report, SectionKeys.Testing).Items, i => i.RuleIds.Contains(id));
    }

    // D. DNA groups feature gate rules under Feature Gates / Authorization / Security.
    [Fact]
    public async Task D_GroupsFeatureGateRules_UnderSecurity()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await SeedRule(db, "When implementing Events backend gates, use IsEventsFeatureEnabled instead of IsVenueMigratedFor.",
            tags: "feature-gate,auth", category: RuleCategory.RepositoryConvention, scopeLevel: ScopeLevel.Repository, scopeValue: "demo");

        await using var scope = db.CreateScope();
        var report = await Service(scope).GenerateAsync(Options());

        Assert.Contains(Section(report, SectionKeys.Security).Items, i => i.RuleIds.Contains(id));
    }

    // E. DNA includes common mistakes from mined candidates or repeated corrections.
    [Fact]
    public async Task E_IncludesCommonMistakes_FromMinedAndCorrections()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedCandidate(db, "Mixing exact object instances with matcher-based Moq setups.");
        var ruleId = await SeedRule(db, "Check feature flags together with feature limits.",
            mistake: "Checking feature flags without feature limits.");
        await SeedOutcome(db, ruleId, OutcomeType.CorrectionRepeated, -0.1);

        await using var scope = db.CreateScope();
        var report = await Service(scope).GenerateAsync(Options());

        var mistakes = Section(report, SectionKeys.CommonMistakes).Items;
        Assert.Contains(mistakes, i => i.Text.Contains("matcher-based Moq", StringComparison.Ordinal));
        Assert.Contains(mistakes, i => i.Text.Contains("without feature limits", StringComparison.Ordinal));
    }

    // F. DNA excludes Archived and Superseded rules.
    [Fact]
    public async Task F_ExcludesArchivedAndSupersededRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var archived = await SeedRule(db, "Archived rule", status: RuleStatus.Archived, confidence: 0.95);
        var superseded = await SeedRule(db, "Superseded rule", status: RuleStatus.Superseded, confidence: 0.95);
        await SeedRule(db, "Active rule", status: RuleStatus.Active, confidence: 0.9);

        await using var scope = db.CreateScope();
        var report = await Service(scope).GenerateAsync(Options());

        var ids = AllRuleIds(report).ToHashSet();
        Assert.DoesNotContain(archived, ids);
        Assert.DoesNotContain(superseded, ids);
    }

    // G. DNA flags stale low-confidence rules as risky knowledge.
    [Fact]
    public async Task G_FlagsStaleLowConfidence_AsRisky()
    {
        await using var db = new TestDatabase();
        await Init(db);
        // Low confidence and never retrieved, created long before AsOf.
        var risky = await SeedRule(db, "Shaky guidance", confidence: 0.2,
            createdAt: June2026.AddDays(-200));

        await using var scope = db.CreateScope();
        var report = await Service(scope).GenerateAsync(Options());

        Assert.Contains(Section(report, SectionKeys.StaleOrRisky).Items, i => i.RuleIds.Contains(risky));
    }

    // H. DNA output is deterministic.
    [Fact]
    public async Task H_Output_IsDeterministic()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedRule(db, "A", confidence: 0.8, tags: "testing", category: RuleCategory.EngineeringLesson);
        await SeedRule(db, "B", confidence: 0.8, tags: "security", category: RuleCategory.EngineeringLesson);

        await using var scope = db.CreateScope();
        var service = Service(scope);
        var first = JsonSerializer.Serialize(await service.GenerateAsync(Options()));
        var second = JsonSerializer.Serialize(await service.GenerateAsync(Options()));

        Assert.Equal(first, second);
    }

    // I. JSON output is valid and stable.
    [Fact]
    public async Task I_JsonOutput_IsValidAndStable()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedRule(db, "Prefer Result<T>", confidence: 0.9, category: RuleCategory.EngineeringLesson);

        var first = new StringWriter();
        var exit = await CommandRouter.RunAsync(["dna", "--json"], db.Services, first);
        Assert.Equal(0, exit);

        using var doc = JsonDocument.Parse(first.ToString());
        var root = doc.RootElement;
        // Stable snake_case keys.
        Assert.True(root.TryGetProperty("generated_at", out _));
        Assert.True(root.TryGetProperty("scope", out _));
        Assert.True(root.TryGetProperty("sections", out var sections));
        Assert.True(root.TryGetProperty("source_counts", out _));
        var item = sections.EnumerateArray()
            .SelectMany(s => s.GetProperty("items").EnumerateArray())
            .First();
        Assert.True(item.TryGetProperty("rule_ids", out _));
        Assert.True(item.TryGetProperty("evidence", out _));

        // Stable structure across runs: only the wall-clock `generated_at` may differ,
        // so compare the substantive `sections` payload verbatim.
        var second = new StringWriter();
        await CommandRouter.RunAsync(["dna", "--json"], db.Services, second);
        using var doc2 = JsonDocument.Parse(second.ToString());
        Assert.Equal(
            sections.GetRawText(),
            doc2.RootElement.GetProperty("sections").GetRawText());
    }

    // J. Markdown output contains expected headings.
    [Fact]
    public async Task J_MarkdownOutput_ContainsExpectedHeadings()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedRule(db, "Prefer Result<T>", confidence: 0.9, category: RuleCategory.EngineeringLesson);

        var output = new StringWriter();
        var exit = await CommandRouter.RunAsync(["dna", "--markdown"], db.Services, output);
        var text = output.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("# Project DNA", text, StringComparison.Ordinal);
        Assert.Contains("## Core Principles", text, StringComparison.Ordinal);
        Assert.Contains("## Testing Patterns", text, StringComparison.Ordinal);
        Assert.Contains("## Feature Gates / Authorization / Security", text, StringComparison.Ordinal);
    }

    // K. Scope filtering works.
    [Fact]
    public async Task K_ScopeFiltering_Works()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var inScope = await SeedRule(db, "Repo A rule", scopeLevel: ScopeLevel.Repository, scopeValue: "repo-a", confidence: 0.9);
        var outOfScope = await SeedRule(db, "Repo B rule", scopeLevel: ScopeLevel.Repository, scopeValue: "repo-b", confidence: 0.9);

        await using var scope = db.CreateScope();
        var report = await Service(scope).GenerateAsync(Options(scopeLevel: ScopeLevel.Repository, scopeValue: "repo-a"));

        var ids = AllRuleIds(report).ToHashSet();
        Assert.Contains(inScope, ids);
        Assert.DoesNotContain(outOfScope, ids);
    }

    // L. Top N limit works.
    [Fact]
    public async Task L_TopN_LimitWorks()
    {
        await using var db = new TestDatabase();
        await Init(db);
        for (var i = 0; i < 8; i++)
        {
            await SeedRule(db, $"Principle {i}", category: RuleCategory.EngineeringLesson, confidence: 0.5 + (i * 0.01));
        }

        await using var scope = db.CreateScope();
        var report = await Service(scope).GenerateAsync(Options(top: 3));

        foreach (var section in report.Sections)
        {
            Assert.True(section.Items.Count <= 3, $"Section {section.Key} exceeded top limit");
        }
    }

    // M. Empty database produces useful empty-state output.
    [Fact]
    public async Task M_EmptyDatabase_ProducesUsefulEmptyState()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using (var scope = db.CreateScope())
        {
            var report = await Service(scope).GenerateAsync(Options());
            // All sections still present (stable structure), every one empty.
            Assert.Equal(9, report.Sections.Count);
            Assert.All(report.Sections, s => Assert.Empty(s.Items));
            Assert.Equal(0, report.SourceCounts.ActiveRules);
        }

        var output = new StringWriter();
        var exit = await CommandRouter.RunAsync(["dna"], db.Services, output);
        Assert.Equal(0, exit);
        Assert.Contains("No rules captured yet", output.ToString(), StringComparison.Ordinal);
    }

    // N. README documents Project DNA and includes CLI examples.
    [Fact]
    public void N_Readme_DocumentsProjectDna()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        Assert.Contains("## Project DNA", readme, StringComparison.Ordinal);
        Assert.Contains("agentrecall dna", readme, StringComparison.Ordinal);
        Assert.Contains("agentrecall dna --markdown", readme, StringComparison.Ordinal);
        Assert.Contains("agentrecall dna --json", readme, StringComparison.Ordinal);
    }

    // O. Command reference includes `dna`.
    [Fact]
    public void O_CommandReference_IncludesDna()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        Assert.Contains("| `dna`", readme, StringComparison.Ordinal);
    }

    /// <summary>Walks up from the test assembly to the repository root (where the solution lives).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("AgentRecall.slnx").Length > 0)
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root (AgentRecall.slnx).");
    }
}
