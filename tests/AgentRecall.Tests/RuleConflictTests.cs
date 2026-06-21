using System.Text.Json;
using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Conflicts;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for deterministic rule conflict detection and explainable resolution:
/// the <see cref="RuleConflictDetector"/>, the <see cref="RuleResolutionService"/>,
/// the inject-context conflict section, and the `rules conflicts` command.
/// </summary>
public class RuleConflictTests
{
    private static readonly DateTimeOffset Fixed = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly RuleConflictDetector Detector = new();
    private static readonly RuleResolutionService Resolver = new();

    private static RecallRule R(
        int id,
        string ruleText,
        ScopeLevel scope = ScopeLevel.Global,
        double confidence = 0.7,
        RuleStatus status = RuleStatus.Active,
        string trigger = "When working on a task",
        string mistake = "",
        DateTimeOffset? updatedAt = null) => new()
    {
        Id = id,
        RuleText = ruleText,
        Trigger = trigger,
        Mistake = mistake,
        ScopeLevel = scope,
        ScopeValue = scope == ScopeLevel.Global ? "" : "AgentRecall",
        Confidence = confidence,
        Status = status,
        UpdatedAt = updatedAt ?? Fixed,
    };

    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task<int> Seed(TestDatabase db, RecallRule rule)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        return (await repo.AddAsync(rule)).Id;
    }

    // A. A direct conflict between opposing approaches is detected.
    [Fact]
    public void A_DirectConflict_IsDetected()
    {
        var conflicts = Detector.Detect(
        [
            R(1, "Use Result<T> for recoverable domain failures.", trigger: "When handling domain failures"),
            R(2, "Throw exceptions for recoverable domain failures.", trigger: "When handling domain failures"),
        ]);

        var conflict = Assert.Single(conflicts);
        Assert.Equal(RuleConflictType.DirectOpposition, conflict.ConflictType);
        Assert.Equal(new[] { 1, 2 }, conflict.RuleIds);
    }

    // B. A repository-specific rule beats a global one.
    [Fact]
    public void B_RepositorySpecific_BeatsGlobal()
    {
        var global = R(1, "Prefer integration tests.", ScopeLevel.Global, trigger: "When testing");
        var repo = R(2, "Prefer unit tests for service logic.", ScopeLevel.Repository, trigger: "When testing service logic");

        var resolution = Resolver.Resolve([global, repo]);

        Assert.Equal(2, resolution.SelectedRuleId);
        Assert.Contains(resolution.Explanation, e => e.Contains("scope", StringComparison.OrdinalIgnoreCase));
    }

    // C. Promoted beats Active when everything else is equal.
    [Fact]
    public void C_Promoted_BeatsActive()
    {
        var active = R(1, "Prefer unit tests.", status: RuleStatus.Active);
        var promoted = R(2, "Prefer unit tests.", status: RuleStatus.Promoted);

        var resolution = Resolver.Resolve([active, promoted]);

        Assert.Equal(2, resolution.SelectedRuleId);
        Assert.Contains(resolution.Explanation, e => e.Contains("Promoted", StringComparison.Ordinal));
    }

    // D. Higher confidence wins when scope and status are equal.
    [Fact]
    public void D_HigherConfidence_Wins()
    {
        var low = R(1, "Prefer unit tests.", confidence: 0.6);
        var high = R(2, "Prefer unit tests.", confidence: 0.9);

        var resolution = Resolver.Resolve([low, high]);

        Assert.Equal(2, resolution.SelectedRuleId);
        Assert.Contains(resolution.Explanation, e => e.Contains("confidence", StringComparison.OrdinalIgnoreCase));
    }

    // E. A more specific trigger wins over a broad one.
    [Fact]
    public void E_SpecificTrigger_BeatsBroadTrigger()
    {
        var broad = R(1, "Prefer unit tests.", trigger: "When testing");
        var specific = R(2, "Prefer unit tests.", trigger: "When testing the OrderService payment refund flow");

        var resolution = Resolver.Resolve([broad, specific]);

        Assert.Equal(2, resolution.SelectedRuleId);
        Assert.Contains(resolution.Explanation, e => e.Contains("trigger", StringComparison.OrdinalIgnoreCase));
    }

    // F. Archived and superseded rules never win, even when otherwise stronger.
    [Theory]
    [InlineData(RuleStatus.Archived)]
    [InlineData(RuleStatus.Superseded)]
    public void F_ArchivedOrSuperseded_NeverWins(RuleStatus deadStatus)
    {
        // The "dead" rule is otherwise dominant: most specific scope, highest confidence.
        var dead = R(1, "Prefer unit tests.", ScopeLevel.File, confidence: 0.99, status: deadStatus);
        var alive = R(2, "Prefer unit tests.", ScopeLevel.Global, confidence: 0.5, status: RuleStatus.Active);

        var resolution = Resolver.Resolve([dead, alive]);

        Assert.Equal(2, resolution.SelectedRuleId);
        Assert.Contains(1, resolution.IgnoredRuleIds);
    }

    // G. inject-context shows the conflict section only when a conflict affects the task.
    [Fact]
    public async Task G_InjectContext_ShowsConflict_WhenItAffectsTask()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var integrationId = await Seed(db, R(0, "Prefer integration tests.", ScopeLevel.Global,
            confidence: 0.64, status: RuleStatus.Active, trigger: "When writing service tests"));
        var unitId = await Seed(db, R(0, "Prefer unit tests for service logic.", ScopeLevel.Repository,
            confidence: 0.91, status: RuleStatus.Promoted, trigger: "When writing service tests for OrderService"));

        var output = new StringWriter();
        var exit = await CommandRouter.RunAsync(["inject-context", "write service tests for OrderService"], db.Services, output);
        var text = output.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("Conflict Detected:", text, StringComparison.Ordinal);
        Assert.Contains(ConflictRenderer.Hint, text, StringComparison.Ordinal);
        Assert.Contains($"#{unitId}", text, StringComparison.Ordinal);
        Assert.Contains($"#{integrationId}", text, StringComparison.Ordinal);
        // The chosen rule is the repository/promoted/higher-confidence one.
        Assert.Contains($"Selected:\n- #{unitId}", text.Replace("\r", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    // H. inject-context shows no conflict section when nothing conflicts.
    [Fact]
    public async Task H_InjectContext_NoConflictSection_WhenNoConflict()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, R(0, "Use a Money value object holding amount and currency.", confidence: 0.9, status: RuleStatus.Promoted, trigger: "When modelling money"));
        await Seed(db, R(0, "Format dates as ISO 8601.", confidence: 0.9, status: RuleStatus.Promoted, trigger: "When formatting dates"));

        var output = new StringWriter();
        var exit = await CommandRouter.RunAsync(["inject-context", "add a refund amount field"], db.Services, output);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("Conflict Detected:", output.ToString(), StringComparison.Ordinal);
    }

    // I. The `rules conflicts` command lists detected conflicts.
    [Fact]
    public async Task I_RulesConflicts_ListsConflicts()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, R(0, "Use Result<T> for recoverable domain failures.", trigger: "When handling domain failures"));
        await Seed(db, R(0, "Throw exceptions for recoverable domain failures.", trigger: "When handling domain failures"));

        var output = new StringWriter();
        var exit = await CommandRouter.RunAsync(["rules", "conflicts"], db.Services, output);
        var text = output.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("conflict(s) detected", text, StringComparison.Ordinal);
        Assert.Contains("Selected:", text, StringComparison.Ordinal);
    }

    // J. The `rules conflicts --json` output is valid JSON.
    [Fact]
    public async Task J_RulesConflicts_Json_IsValid()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, R(0, "Use Result<T> for recoverable domain failures.", trigger: "When handling domain failures"));
        await Seed(db, R(0, "Throw exceptions for recoverable domain failures.", trigger: "When handling domain failures"));

        var output = new StringWriter();
        var exit = await CommandRouter.RunAsync(["rules", "conflicts", "--json"], db.Services, output);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var first = doc.RootElement[0];
        Assert.True(first.GetProperty("selectedRuleId").GetInt32() > 0);
        Assert.False(string.IsNullOrEmpty(first.GetProperty("conflictId").GetString()));
    }

    // K. The resolution score breakdown is deterministic.
    [Fact]
    public void K_ScoreBreakdown_IsDeterministic()
    {
        RecallRule[] Rules() =>
        [
            R(1, "Prefer integration tests.", ScopeLevel.Global, confidence: 0.64),
            R(2, "Prefer unit tests for service logic.", ScopeLevel.Repository, confidence: 0.91, status: RuleStatus.Promoted),
        ];

        var first = JsonSerializer.Serialize(Resolver.Resolve(Rules()));
        var second = JsonSerializer.Serialize(Resolver.Resolve(Rules()));

        Assert.Equal(first, second);
    }

    // --- Detector branch coverage beyond the spec's A–L ---

    // Opposite polarity on the same subject ("use X" vs "do not use X").
    [Fact]
    public void Detect_OppositePolarity_IsDirectOpposition()
    {
        var conflicts = Detector.Detect(
        [
            R(1, "Use the repository pattern for data access.", trigger: "When accessing data"),
            R(2, "Do not use the repository pattern for data access.", trigger: "When accessing data"),
        ]);

        Assert.Equal(RuleConflictType.DirectOpposition, Assert.Single(conflicts).ConflictType);
    }

    // The spec's mock example: mock vs do-not-mock.
    [Fact]
    public void Detect_MockVsDoNotMock_IsDirectOpposition()
    {
        var conflicts = Detector.Detect(
        [
            R(1, "Mock external services in tests.", trigger: "When writing tests"),
            R(2, "Do not mock external services in tests.", trigger: "When writing tests"),
        ]);

        Assert.Equal(RuleConflictType.DirectOpposition, Assert.Single(conflicts).ConflictType);
    }

    // One rule recommends exactly what another names as its anti-pattern.
    [Fact]
    public void Detect_ActionMatchesOtherAvoid_IsPreferredVsAvoided()
    {
        var conflicts = Detector.Detect(
        [
            R(1, "Use the repository pattern.", trigger: "When accessing data"),
            R(2, "Access the database directly.", trigger: "When accessing data", mistake: "Avoid the repository pattern."),
        ]);

        Assert.Equal(RuleConflictType.PreferredVsAvoided, Assert.Single(conflicts).ConflictType);
    }

    // Near-identical guidance whose lifecycle status disagrees.
    [Fact]
    public void Detect_SameGuidanceDifferentStatus_IsStatusConflict()
    {
        var conflicts = Detector.Detect(
        [
            R(1, "Use the repository pattern.", trigger: "When accessing data", status: RuleStatus.Active),
            R(2, "Use the repository pattern.", trigger: "When accessing data", status: RuleStatus.Superseded),
        ]);

        Assert.Equal(RuleConflictType.StatusConflict, Assert.Single(conflicts).ConflictType);
    }

    // Near-identical guidance carried at different scope levels.
    [Fact]
    public void Detect_SameGuidanceDifferentScope_IsBroaderVsSpecific()
    {
        var conflicts = Detector.Detect(
        [
            R(1, "Use the repository pattern.", ScopeLevel.Global, trigger: "When accessing data"),
            R(2, "Use the repository pattern.", ScopeLevel.Repository, trigger: "When accessing data"),
        ]);

        Assert.Equal(RuleConflictType.BroaderVsSpecific, Assert.Single(conflicts).ConflictType);
    }

    // Unrelated or agreeing rules must NOT be reported as a conflict — otherwise the
    // injection pass would prune a legitimate rule.
    [Fact]
    public void Detect_UnrelatedOrAgreeingRules_NoConflict()
    {
        // Different subjects entirely.
        Assert.Empty(Detector.Detect(
        [
            R(1, "Use Result<T> for recoverable failures.", trigger: "When handling failures"),
            R(2, "Format dates as ISO 8601.", trigger: "When formatting dates"),
        ]));

        // Same subject, same direction — agreement, not conflict.
        Assert.Empty(Detector.Detect(
        [
            R(1, "Use unit tests for services.", trigger: "When testing services"),
            R(2, "Prefer unit tests for service logic.", trigger: "When testing service logic"),
        ]));
    }

    // L. Resolution never mutates the rules it scores.
    [Fact]
    public void L_Resolution_DoesNotMutateRules()
    {
        var a = R(1, "Prefer integration tests.", ScopeLevel.Global, confidence: 0.64, status: RuleStatus.Active);
        var b = R(2, "Prefer unit tests.", ScopeLevel.Repository, confidence: 0.91, status: RuleStatus.Promoted);

        _ = Resolver.Resolve([a, b]);

        Assert.Equal(RuleStatus.Active, a.Status);
        Assert.Equal(0.64, a.Confidence, 3);
        Assert.Equal(ScopeLevel.Global, a.ScopeLevel);
        Assert.Equal(RuleStatus.Promoted, b.Status);
        Assert.Equal(0.91, b.Confidence, 3);
        Assert.Equal(Fixed, b.UpdatedAt);
    }
}
