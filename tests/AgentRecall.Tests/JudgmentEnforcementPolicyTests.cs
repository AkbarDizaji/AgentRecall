using AgentRecall.Core.Finalization;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// The pure decision behind enforced judgment: given what is known about a turn, does it still owe
/// AgentRecall a verdict? No IO, no clock, no content inspection beyond the size floor — so every
/// case is a table row. This is deliberately not a capture decision: nothing here can choose to
/// remember something.
/// </summary>
public class JudgmentEnforcementPolicyTests
{
    private static JudgmentEnforcementFacts Substantive(
        bool hasJudgment = false,
        bool alreadyJudged = false,
        int priorAttempts = 0,
        bool hostSaysResumed = false,
        int characters = 500) => new()
    {
        HasSuppliedJudgment = hasJudgment,
        AlreadyJudged = alreadyJudged,
        HasPrompt = true,
        HasAssistantResponse = true,
        TurnCharacters = characters,
        PriorAttempts = priorAttempts,
        HostSaysResumed = hostSaysResumed,
    };

    [Fact]
    public void SuppliedJudgment_Finalizes()
    {
        var decision = JudgmentEnforcementPolicy.Decide(
            Substantive(hasJudgment: true), JudgmentEnforcementMode.Substantive);

        Assert.Equal(JudgmentEnforcementAction.Finalize, decision.Action);
        Assert.Equal(JudgmentEnforcementPolicy.JudgmentPresentReason, decision.Reason);
    }

    [Fact]
    public void AlreadyJudgedTurn_Finalizes()
    {
        var decision = JudgmentEnforcementPolicy.Decide(
            Substantive(alreadyJudged: true), JudgmentEnforcementMode.Substantive);

        Assert.Equal(JudgmentEnforcementAction.Finalize, decision.Action);
        Assert.Equal(JudgmentEnforcementPolicy.AlreadyJudgedReason, decision.Reason);
    }

    [Fact]
    public void UnjudgedSubstantiveTurn_RequestsJudgment()
    {
        var decision = JudgmentEnforcementPolicy.Decide(Substantive(), JudgmentEnforcementMode.Substantive);

        Assert.Equal(JudgmentEnforcementAction.RequestJudgment, decision.Action);
    }

    [Fact]
    public void ModeOff_NeverRequests()
    {
        var decision = JudgmentEnforcementPolicy.Decide(Substantive(), JudgmentEnforcementMode.Off);

        Assert.Equal(JudgmentEnforcementAction.Finalize, decision.Action);
        Assert.Equal(JudgmentEnforcementPolicy.EnforcementOffReason, decision.Reason);
    }

    [Theory]
    [InlineData(199, JudgmentEnforcementAction.Finalize)]
    [InlineData(200, JudgmentEnforcementAction.RequestJudgment)]
    public void SizeFloor_IsTheOnlySubstantivenessTest(int characters, JudgmentEnforcementAction expected)
    {
        var decision = JudgmentEnforcementPolicy.Decide(
            Substantive(characters: characters), JudgmentEnforcementMode.Substantive);

        Assert.Equal(expected, decision.Action);
    }

    [Fact]
    public void SubstantiveMode_RequiresBothHalvesOfTheExchange()
    {
        var responseOnly = Substantive() with { HasPrompt = false };
        var promptOnly = Substantive() with { HasAssistantResponse = false };

        Assert.Equal(
            JudgmentEnforcementAction.Finalize,
            JudgmentEnforcementPolicy.Decide(responseOnly, JudgmentEnforcementMode.Substantive).Action);
        Assert.Equal(
            JudgmentEnforcementAction.Finalize,
            JudgmentEnforcementPolicy.Decide(promptOnly, JudgmentEnforcementMode.Substantive).Action);
    }

    [Fact]
    public void AlwaysMode_AsksAboutAShortTurn_ButStillNeedsAPrompt()
    {
        var tiny = Substantive(characters: 4) with { HasAssistantResponse = false };
        var promptless = Substantive() with { HasPrompt = false };

        Assert.Equal(
            JudgmentEnforcementAction.RequestJudgment,
            JudgmentEnforcementPolicy.Decide(tiny, JudgmentEnforcementMode.Always).Action);
        Assert.Equal(
            JudgmentEnforcementAction.Finalize,
            JudgmentEnforcementPolicy.Decide(promptless, JudgmentEnforcementMode.Always).Action);
    }

    [Fact]
    public void ExhaustedAttempts_ProceedUnjudged()
    {
        var decision = JudgmentEnforcementPolicy.Decide(
            Substantive(priorAttempts: 1), JudgmentEnforcementMode.Substantive);

        Assert.Equal(JudgmentEnforcementAction.ProceedUnjudged, decision.Action);
        Assert.Equal(JudgmentEnforcementPolicy.AttemptsExhaustedReason, decision.Reason);
    }

    [Fact]
    public void HostResumedSignal_ProceedUnjudged_EvenOnTheFirstAttempt()
    {
        var decision = JudgmentEnforcementPolicy.Decide(
            Substantive(hostSaysResumed: true), JudgmentEnforcementMode.Substantive);

        Assert.Equal(JudgmentEnforcementAction.ProceedUnjudged, decision.Action);
        Assert.Equal(JudgmentEnforcementPolicy.HostResumedReason, decision.Reason);
    }

    // A judgment in hand outranks every guard, so a verdict submitted on the last allowed attempt
    // is still honoured rather than discarded as "too late".
    [Fact]
    public void SuppliedJudgment_OutranksTheLoopGuards()
    {
        var decision = JudgmentEnforcementPolicy.Decide(
            Substantive(hasJudgment: true, priorAttempts: 9, hostSaysResumed: true),
            JudgmentEnforcementMode.Substantive);

        Assert.Equal(JudgmentEnforcementAction.Finalize, decision.Action);
    }

    // Zero allowed asks behaves as a disabled block rather than as an unbounded one.
    [Fact]
    public void ZeroMaxAttempts_NeverRequests()
    {
        var decision = JudgmentEnforcementPolicy.Decide(
            Substantive(), JudgmentEnforcementMode.Substantive, maxAttempts: 0);

        Assert.Equal(JudgmentEnforcementAction.ProceedUnjudged, decision.Action);
    }

    [Fact]
    public void MoreAttemptsAllowed_KeepsAsking()
    {
        var decision = JudgmentEnforcementPolicy.Decide(
            Substantive(priorAttempts: 1), JudgmentEnforcementMode.Substantive, maxAttempts: 2);

        Assert.Equal(JudgmentEnforcementAction.RequestJudgment, decision.Action);
    }
}
