using AgentRecall.Core.Capture;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Outcomes;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// A trigger is the surface retrieval matches on, so its quality is whether it can match work the
/// rule applies to — and nothing else. These cover both ways that fails, using the two real rules
/// that made the failures visible: a trigger written as a task description that then rode along on
/// a third of every retrieval, and a trigger nothing ever matched.
/// </summary>
public class TriggerQualityTests
{
    [Theory]
    [InlineData("when releasing AgentRecall (the user asks to tag, or a feature is ready to publish)")]
    [InlineData("when editing README.md in this repo")]
    [InlineData("If a build fails with NU1900 naming a private feed")]
    [InlineData("before pushing a signed commit")]
    public void Inspect_AConditionalTrigger_IsUsable(string trigger)
    {
        Assert.Equal(TriggerProblem.None, TriggerQuality.Inspect(trigger));
    }

    // The real #1: a task someone typed once, stored as a trigger. It matches on whatever generic
    // words that task happened to use, which is why it was injected on unrelated turns forever.
    [Fact]
    public void Inspect_ATaskDescription_IsNotConditional()
    {
        Assert.Equal(
            TriggerProblem.NotConditional,
            TriggerQuality.Inspect(
                "Refactor Invoice domain model to use a Money value object instead of separate decimal fields"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("when refactoring")]
    public void Inspect_TooFewWords_IdentifiesNoSituation(string? trigger)
    {
        Assert.Equal(TriggerProblem.TooShort, TriggerQuality.Inspect(trigger));
    }

    [Fact]
    public void Inspect_ATriggerItsActionSimplyContinues_IsARestatement()
    {
        const string Trigger = "when adding a feature gate to the Events backend";
        const string Action = "When adding a feature gate to the Events backend: use the canonical gate definition.";

        Assert.Equal(TriggerProblem.RestatesTheAction, TriggerQuality.Inspect(Trigger, Action));
    }

    [Fact]
    public void Inspect_APunctuationOnlyDifference_StillCountsAsARestatement()
    {
        Assert.Equal(
            TriggerProblem.RestatesTheAction,
            TriggerQuality.Inspect("when writing a migration", "When, writing a migration — always guard it."));
    }

    [Fact]
    public void Inspect_AParagraph_IsTooLong()
    {
        var trigger = "when " + string.Join(' ', Enumerable.Repeat("something", TriggerQuality.MaxWords + 1));

        Assert.Equal(TriggerProblem.TooLong, TriggerQuality.Inspect(trigger));
    }

    [Fact]
    public void Describe_ExplainsWhatToDoAboutIt()
    {
        Assert.Contains("when/if", TriggerQuality.Describe(TriggerProblem.NotConditional), StringComparison.Ordinal);
        Assert.Contains("repeats", TriggerQuality.Describe(TriggerProblem.RestatesTheAction), StringComparison.Ordinal);
    }
}

/// <summary>
/// The audit reads the ledger rather than the prose: injections against outcomes. Both failure
/// modes it reports were measured on the real database before this existed — one rule injected in
/// 24 of 81 retrievals without ever applying, another never retrieved at all.
/// </summary>
public class RuleTriggerAuditTests
{
    private static RecallRule Rule(int id, string trigger, string action = "Do the thing.", bool alwaysApply = false) =>
        new()
        {
            Id = id,
            AlwaysApply = alwaysApply,
            Trigger = trigger,
            RuleText = action,
            Mistake = "Avoid the other thing.",
            TechnicalContext = "",
            Tags = "",
            Confidence = 0.6,
            Status = RuleStatus.Active,
            ScopeLevel = ScopeLevel.Global,
            ScopeValue = "",
        };

    [Fact]
    public void Audit_InjectedOftenAndNeverAccepted_IsNoisy()
    {
        var reports = RuleTriggerAudit.Audit(
        [
            new RuleTriggerHistory(Rule(1, "when working on payments"), Injections: 24, Accepted: 0, Ignored: 4),
        ]);

        var report = Assert.Single(reports);
        Assert.Contains(TriggerFinding.Noisy, report.Findings);
        Assert.False(report.IsHealthy);
    }

    // A rule that helped even once is not noise, however often it is also ignored: a rule that
    // applies rarely but decisively is the point of keeping it.
    [Fact]
    public void Audit_InjectedOftenButAcceptedOnce_IsNotNoisy()
    {
        var reports = RuleTriggerAudit.Audit(
        [
            new RuleTriggerHistory(Rule(1, "when running dotnet build in this repo"), 34, Accepted: 1, Ignored: 6),
        ]);

        Assert.DoesNotContain(TriggerFinding.Noisy, Assert.Single(reports).Findings);
    }

    // Silence is not evidence. A rule injected for weeks that nobody has reported on may be fine;
    // condemning it would punish good rules for a thin ledger.
    [Fact]
    public void Audit_InjectedOftenButBarelyReportedOn_IsNotYetNoise()
    {
        var reports = RuleTriggerAudit.Audit(
        [
            new RuleTriggerHistory(Rule(22, "when short-circuiting a write path on a content hash"), 25, 0, 1),
        ]);

        Assert.DoesNotContain(TriggerFinding.Noisy, Assert.Single(reports).Findings);
    }

    [Fact]
    public void Audit_BelowTheInjectionFloor_IsNotYetJudged()
    {
        var reports = RuleTriggerAudit.Audit(
        [
            new RuleTriggerHistory(
                Rule(1, "when writing a migration guard"),
                RuleTriggerAudit.NoisyInjectionFloor - 1,
                0,
                RuleTriggerAudit.NoisyIgnoredFloor + 2),
        ]);

        Assert.DoesNotContain(TriggerFinding.Noisy, Assert.Single(reports).Findings);
    }

    [Fact]
    public void Audit_NeverInjected_IsDormant()
    {
        var reports = RuleTriggerAudit.Audit(
        [
            new RuleTriggerHistory(Rule(5, "when writing Console.WriteLine output statements in C#"), 0, 0, 0),
        ]);

        Assert.Contains(TriggerFinding.Dormant, Assert.Single(reports).Findings);
    }

    // A standing rule is injected regardless of its words, so never being matched says nothing
    // about its trigger and must not read as dormant.
    [Fact]
    public void Audit_AnAlwaysApplyRule_IsNotDormant()
    {
        var reports = RuleTriggerAudit.Audit(
        [
            new RuleTriggerHistory(Rule(34, "when creating any commit on the user's behalf", alwaysApply: true), 0, 0, 0),
        ]);

        Assert.True(Assert.Single(reports).IsHealthy);
    }

    // The real #15/#16: one lesson, two triggers, both injected on every matching turn.
    [Fact]
    public void Audit_TwoRulesWithTheSameAction_FlagsTheLaterOneAsADuplicate()
    {
        const string Action = "Guard every refund path with an idempotency key.";

        var reports = RuleTriggerAudit.Audit(
        [
            new RuleTriggerHistory(Rule(15, "when editing the payments file", Action), 18, 0, 4),
            new RuleTriggerHistory(Rule(16, "when working on refunds", Action), 18, 0, 4),
        ]);

        var duplicate = Assert.Single(reports, r => r.Findings.Contains(TriggerFinding.Duplicate));
        Assert.Equal(16, duplicate.RuleId);
        Assert.Equal(15, duplicate.DuplicateOf);
    }

    [Fact]
    public void Audit_AnUnusableTrigger_IsReportedWithItsProblem()
    {
        var reports = RuleTriggerAudit.Audit(
        [
            new RuleTriggerHistory(Rule(1, "Refactor the Invoice model to use a Money value object"), 2, 0, 0),
        ]);

        var report = Assert.Single(reports);
        Assert.Contains(TriggerFinding.Unusable, report.Findings);
        Assert.Equal(TriggerProblem.NotConditional, report.Problem);
    }

    [Fact]
    public void Audit_AHealthyTrigger_ReportsNothing()
    {
        var reports = RuleTriggerAudit.Audit(
        [
            new RuleTriggerHistory(Rule(18, "when running dotnet build in this repo outside CI"), 34, 2, 1),
        ]);

        Assert.True(Assert.Single(reports).IsHealthy);
    }

    // Ordered so the most expensive rule is read first: noise costs context on every injection.
    [Fact]
    public void Audit_OrdersTheNoisiestFirst()
    {
        var reports = RuleTriggerAudit.Audit(
        [
            new RuleTriggerHistory(Rule(2, "when doing something occasional"), 0, 0, 0),
            new RuleTriggerHistory(Rule(1, "when doing something common"), 30, 0, 5),
        ]);

        Assert.Equal(1, reports[0].RuleId);
    }
}

/// <summary>
/// Confidence moves slowly by design, so a rule at 0.45 is still injected on every turn whose words
/// match. The dampener lets a rule's own record do what confidence does not, without ever shutting
/// a rule out entirely.
/// </summary>
public class RuleEffectivenessTests
{
    [Fact]
    public void Factor_WithTooFewReports_ChangesNothing()
    {
        Assert.Equal(1.0, RuleEffectiveness.Factor(accepted: 0, ignored: 0));
        Assert.Equal(1.0, RuleEffectiveness.Factor(0, RuleEffectiveness.MinimumReports - 1));
    }

    [Fact]
    public void Factor_IgnoredRepeatedlyAndNeverAccepted_IsDampenedToTheFloor()
    {
        Assert.Equal(RuleEffectiveness.Floor, RuleEffectiveness.Factor(accepted: 0, ignored: 12));
    }

    [Fact]
    public void Factor_AcceptedOften_IsNotDampened()
    {
        Assert.Equal(1.0, RuleEffectiveness.Factor(accepted: 10, ignored: 0));
    }

    [Fact]
    public void Factor_NeverFallsBelowTheFloor()
    {
        Assert.True(RuleEffectiveness.Factor(0, 1000) >= RuleEffectiveness.Floor);
    }

    // A rule that has helped at all is left alone, however often it is also ignored: firing rarely
    // but decisively is the point of keeping it, and the turns between are not evidence against it.
    [Fact]
    public void Factor_AcceptedEvenOnce_IsNeverDampened()
    {
        Assert.Equal(1.0, RuleEffectiveness.Factor(accepted: 1, ignored: 20));
        Assert.Equal(1.0, RuleEffectiveness.Factor(accepted: 2, ignored: 4));
    }

    // Never accepted: the dampener ramps in rather than dropping to the floor on the first report.
    [Fact]
    public void Factor_NeverAccepted_RampsDownWithTheIgnores()
    {
        var early = RuleEffectiveness.Factor(0, RuleEffectiveness.MinimumReports);
        var later = RuleEffectiveness.Factor(0, RuleEffectiveness.MinimumReports + 2);

        Assert.True(early < 1.0, "past the threshold a never-accepted rule should be dampened.");
        Assert.True(later < early, "more evidence of not applying should dampen it further.");
        Assert.True(later > RuleEffectiveness.Floor, "it should reach the floor, not start there.");
    }
}
