using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Policy;
using Xunit;

namespace AgentRecall.Tests;

public class PolicyTests
{
    /// <summary>Repository stub: the pure <see cref="PolicyEngine.Resolve"/> never touches it.</summary>
    private sealed class UnusedRuleRepository : IRecallRuleRepository
    {
        public Task<RecallRule> AddAsync(RecallRule entity, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RecallRule?> GetAsync(int id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RecallRule>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RecallRule> UpdateAsync(RecallRule entity, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    // A fixed clock so CreatedAt ordering in tests is explicit, not wall-clock.
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static int _nextId = 1;

    private static RecallRule Rule(
        string ruleText,
        RuleStatus status = RuleStatus.Active,
        double confidence = 0.5,
        int priority = 0,
        ScopeLevel scopeLevel = ScopeLevel.Global,
        string scopeValue = "",
        bool deprecated = false,
        int? supersedesRuleId = null,
        int? supersededById = null,
        int createdOffsetDays = 0,
        int? id = null)
    {
        return new RecallRule
        {
            Id = id ?? _nextId++,
            Trigger = "t",
            RuleText = ruleText,
            Mistake = "",
            TechnicalContext = "",
            Tags = "",
            Confidence = confidence,
            Priority = priority,
            Status = status,
            ScopeLevel = scopeLevel,
            ScopeValue = scopeValue,
            Deprecated = deprecated,
            SupersedesRuleId = supersedesRuleId,
            SupersededById = supersededById,
            CreatedAt = T0.AddDays(createdOffsetDays),
        };
    }

    private static PolicyEngine Engine() => new(new UnusedRuleRepository());

    private static bool Has(IReadOnlyList<RuleVerdict> verdicts, int id) =>
        verdicts.Any(v => v.Rule.Id == id);

    [Fact]
    public void DirectConflict_ChoosesWinner_AndIgnoresLoser()
    {
        // Same subject, opposite guidance. The newer rule should win (equal scope,
        // priority; confidence equal) so recency decides.
        var use = Rule("Use the repository pattern.", createdOffsetDays: 0, id: 1);
        var dont = Rule("Do not use the repository pattern.", createdOffsetDays: 5, id: 2);

        var result = Engine().Resolve([use, dont], PolicyContext.None);

        Assert.Single(result.Conflicts);
        Assert.Single(result.Effective);
        Assert.Single(result.Ignored);

        Assert.Equal(2, result.Conflicts[0].Winner.Id);
        Assert.True(Has(result.Effective, 2));
        Assert.True(Has(result.Ignored, 1));
        Assert.Contains("repository", result.Conflicts[0].Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonConflictingRules_AreBothEffective()
    {
        var a = Rule("Use parameterized SQL queries.", id: 10);
        var b = Rule("Use ISO 8601 for dates.", id: 11);

        var result = Engine().Resolve([a, b], PolicyContext.None);

        Assert.Empty(result.Conflicts);
        Assert.Equal(2, result.Effective.Count);
        Assert.Empty(result.Ignored);
    }

    [Fact]
    public void SupersededRule_IsIgnored_InFavorOfSuperseder()
    {
        // Rule 21 explicitly supersedes rule 20. No textual conflict needed.
        var old = Rule("Validate input at the controller.", id: 20);
        var replacement = Rule("Validate input at the domain boundary.", supersedesRuleId: 20, id: 21);

        var result = Engine().Resolve([old, replacement], PolicyContext.None);

        Assert.True(Has(result.Effective, 21));
        Assert.True(Has(result.Ignored, 20));
        Assert.Contains(result.Ignored, v => v.Rule.Id == 20 && v.Reason.Contains("Superseded"));
    }

    [Fact]
    public void SupersededByStatus_IsIgnored_WhenReplacementPresent()
    {
        // The reverse relationship: old records who replaced it.
        var old = Rule("Old guidance.", status: RuleStatus.Superseded, supersededById: 31, id: 30);
        var replacement = Rule("New guidance.", id: 31);

        var result = Engine().Resolve([old, replacement], PolicyContext.None);

        Assert.True(Has(result.Effective, 31));
        Assert.True(Has(result.Ignored, 30));
    }

    [Fact]
    public void ProjectRule_Overrides_GlobalRule_OnConflict()
    {
        // Global rule is newer and more confident, but the project-scoped rule
        // must still win because scope precedence is checked first.
        var global = Rule("Use the repository pattern.", confidence: 0.9, createdOffsetDays: 10,
            scopeLevel: ScopeLevel.Global, id: 40);
        var project = Rule("Do not use the repository pattern.", confidence: 0.4, createdOffsetDays: 0,
            scopeLevel: ScopeLevel.Repository, scopeValue: "AgentRecall", id: 41);

        var context = new PolicyContext { ScopeLevel = ScopeLevel.Repository, ScopeValue = "AgentRecall" };
        var result = Engine().Resolve([global, project], context);

        Assert.Single(result.Conflicts);
        Assert.Equal(41, result.Conflicts[0].Winner.Id);
        Assert.True(Has(result.Effective, 41));
        Assert.True(Has(result.Ignored, 40));
        Assert.Contains("project", result.Conflicts[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfidenceTie_BrokenByPriority()
    {
        // Equal confidence, equal scope, equal CreatedAt: priority decides.
        var low = Rule("Use the repository pattern.", confidence: 0.7, priority: 1, createdOffsetDays: 0, id: 50);
        var high = Rule("Do not use the repository pattern.", confidence: 0.7, priority: 5, createdOffsetDays: 0, id: 51);

        var result = Engine().Resolve([low, high], PolicyContext.None);

        Assert.Single(result.Conflicts);
        Assert.Equal(51, result.Conflicts[0].Winner.Id);
        Assert.Contains("priority", result.Conflicts[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HigherConfidence_Wins_WhenScopePriorityAndRecencyTie()
    {
        var lower = Rule("Use the repository pattern.", confidence: 0.6, priority: 0, createdOffsetDays: 3, id: 60);
        var higher = Rule("Do not use the repository pattern.", confidence: 0.95, priority: 0, createdOffsetDays: 3, id: 61);

        var result = Engine().Resolve([lower, higher], PolicyContext.None);

        Assert.Equal(61, result.Conflicts[0].Winner.Id);
        Assert.Contains("confidence", result.Conflicts[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FullTie_ResolvesDeterministically_ByRuleId()
    {
        var a = Rule("Use the repository pattern.", confidence: 0.7, priority: 0, createdOffsetDays: 0, id: 70);
        var b = Rule("Do not use the repository pattern.", confidence: 0.7, priority: 0, createdOffsetDays: 0, id: 71);

        var first = Engine().Resolve([a, b], PolicyContext.None);
        var second = Engine().Resolve([b, a], PolicyContext.None);

        // Order of input must not change the outcome.
        Assert.Equal(70, first.Conflicts[0].Winner.Id);
        Assert.Equal(70, second.Conflicts[0].Winner.Id);
    }

    [Fact]
    public void DeprecatedRule_IsAlwaysIgnored()
    {
        var deprecated = Rule("Use the repository pattern.", deprecated: true, id: 80);
        var active = Rule("Use dependency injection.", id: 81);

        var result = Engine().Resolve([deprecated, active], PolicyContext.None);

        Assert.True(Has(result.Ignored, 80));
        Assert.True(Has(result.Effective, 81));
        Assert.Contains(result.Ignored, v => v.Rule.Id == 80 && v.Reason.Contains("Deprecated"));
    }

    [Fact]
    public void NonActiveStatuses_AreIgnored()
    {
        var pending = Rule("Use feature flags.", status: RuleStatus.Pending, id: 90);
        var archived = Rule("Use feature flags carefully.", status: RuleStatus.Archived, id: 91);
        var promoted = Rule("Use feature flags consistently.", status: RuleStatus.Promoted, id: 92);

        var result = Engine().Resolve([pending, archived, promoted], PolicyContext.None);

        Assert.True(Has(result.Effective, 92));
        Assert.True(Has(result.Ignored, 90));
        Assert.True(Has(result.Ignored, 91));
    }

    [Fact]
    public void Explanation_SummarizesDecisions()
    {
        var use = Rule("Use the repository pattern.", createdOffsetDays: 0, id: 100);
        var dont = Rule("Do not use the repository pattern.", createdOffsetDays: 5, id: 101);

        var result = Engine().Resolve([use, dont], PolicyContext.None);

        Assert.Contains("effective", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Conflict", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#101", result.Explanation);
    }

    [Fact]
    public void EmptyCandidateSet_YieldsEmptyResolution()
    {
        var result = Engine().Resolve([], PolicyContext.None);

        Assert.Empty(result.Effective);
        Assert.Empty(result.Ignored);
        Assert.Empty(result.Conflicts);
    }
}
