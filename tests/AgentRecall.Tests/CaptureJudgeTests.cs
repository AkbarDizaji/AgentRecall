using AgentRecall.Core.Capture;
using AgentRecall.Core.Capture.Judge;
using AgentRecall.Core.Domain;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Pure, offline unit tests for the semantic-capture-judge core: the deterministic
/// <see cref="CaptureJudgeValidator"/> (structural contract only) and the
/// <see cref="CaptureJudgeDecisionMapper"/> (confidence-threshold and reason policy). No DB,
/// no LLM — the model's verdict is supplied directly and the system only validates + maps it.
/// </summary>
public class CaptureJudgeTests
{
    private static NormalizedRule SoundRule() => new()
    {
        Title = "Consume the payment token",
        Condition = "when a validator requires a payment method token",
        Action = "consume, attach, or persist it before claiming the card is saved",
        Avoid = "validate-and-drop flows",
        Because = "they create false guarantees for later off-session charging",
        Scope = "workspace",
        Tags = ["payments"],
    };

    private static CaptureJudgeVerdict Verdict(
        JudgeDecision decision = JudgeDecision.Capture,
        JudgeMemoryType memoryType = JudgeMemoryType.EngineeringLesson,
        double confidence = 0.9,
        JudgeCaptureReason reason = JudgeCaptureReason.ObservedAgentFailure,
        NormalizedRule? rule = null,
        int? target = null,
        string? whyNotSaved = null,
        string? dedupeNotes = null) => new()
    {
        Decision = decision,
        MemoryType = memoryType,
        Confidence = confidence,
        CaptureReason = reason,
        TargetExistingRuleId = target,
        NormalizedRule = rule,
        WhyNotSaved = whyNotSaved,
        DedupeNotes = dedupeNotes,
    };

    private static CaptureJudgeOutcome Map(CaptureJudgeVerdict verdict) =>
        CaptureJudgeDecisionMapper.Map(verdict, CaptureJudgeValidator.Validate(verdict));

    // ---- Validator ------------------------------------------------------------

    [Fact]
    public void Validator_SoundCapture_IsValid()
    {
        Assert.True(CaptureJudgeValidator.Validate(Verdict(rule: SoundRule())).IsValid);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Validator_ConfidenceOutOfRange_IsInvalid(double confidence)
    {
        Assert.False(CaptureJudgeValidator.Validate(Verdict(confidence: confidence, rule: SoundRule())).IsValid);
    }

    [Fact]
    public void Validator_CaptureMissingCondition_IsHardInvalid()
    {
        var rule = SoundRule() with { Condition = "" };
        var result = CaptureJudgeValidator.Validate(Verdict(rule: rule));
        Assert.False(result.IsValid);
        Assert.False(result.DowngradeToSuggest);
    }

    [Fact]
    public void Validator_CaptureMissingBecause_DowngradesToSuggest()
    {
        var rule = SoundRule() with { Because = "" };
        var result = CaptureJudgeValidator.Validate(Verdict(rule: rule));
        Assert.False(result.IsValid);
        Assert.True(result.DowngradeToSuggest);
    }

    [Fact]
    public void Validator_SkipMissingWhyNotSaved_IsInvalid()
    {
        Assert.False(CaptureJudgeValidator.Validate(Verdict(decision: JudgeDecision.Skip)).IsValid);
        Assert.True(CaptureJudgeValidator.Validate(
            Verdict(decision: JudgeDecision.Skip, whyNotSaved: "assistant prose")).IsValid);
    }

    [Fact]
    public void Validator_ReinforceRequiresTargetAndNotes()
    {
        Assert.False(CaptureJudgeValidator.Validate(
            Verdict(decision: JudgeDecision.ReinforceExisting, dedupeNotes: "same rule")).IsValid);
        Assert.False(CaptureJudgeValidator.Validate(
            Verdict(decision: JudgeDecision.ReinforceExisting, target: 7)).IsValid);
        Assert.True(CaptureJudgeValidator.Validate(
            Verdict(decision: JudgeDecision.ReinforceExisting, target: 7, dedupeNotes: "same rule")).IsValid);
    }

    [Fact]
    public void Validator_SupersedeRequiresTargetAndSoundRule()
    {
        Assert.False(CaptureJudgeValidator.Validate(
            Verdict(decision: JudgeDecision.SupersedeExisting, rule: SoundRule())).IsValid);
        Assert.True(CaptureJudgeValidator.Validate(
            Verdict(decision: JudgeDecision.SupersedeExisting, target: 3, rule: SoundRule())).IsValid);
    }

    [Fact]
    public void Validator_OverLongField_IsInvalid()
    {
        var rule = SoundRule() with { Action = new string('x', CaptureJudgeValidator.FieldMaxLength + 1) };
        Assert.False(CaptureJudgeValidator.Validate(Verdict(rule: rule)).IsValid);
    }

    [Fact]
    public void Validator_RuleEchoingRawTurnText_IsInvalid()
    {
        var raw = "Off topic: in the registration modal we change the button according to validation.";
        var rule = SoundRule() with { Action = raw };
        var input = new CaptureJudgeInput { AssistantSummary = raw };
        Assert.False(CaptureJudgeValidator.Validate(Verdict(rule: rule), input).IsValid);
    }

    // ---- Mapper: explicit intents --------------------------------------------

    [Fact]
    public void Mapper_ExplicitDoNotSave_Skips()
    {
        var outcome = Map(Verdict(reason: JudgeCaptureReason.ExplicitUserDoNotSave,
            decision: JudgeDecision.Skip, whyNotSaved: "user asked not to save"));
        Assert.Equal(JudgePersistAction.Skip, outcome.Action);
    }

    [Fact]
    public void Mapper_ExplicitSave_CapturesEvenAtLowConfidence()
    {
        var outcome = Map(Verdict(reason: JudgeCaptureReason.ExplicitUserSave, confidence: 0.2, rule: SoundRule()));
        Assert.Equal(JudgePersistAction.AutoCapture, outcome.Action);
        Assert.Equal(RuleStatus.Active, outcome.Status);
    }

    [Fact]
    public void Mapper_ExplicitSave_OfCodeFact_StillCaptures()
    {
        var outcome = Map(Verdict(
            reason: JudgeCaptureReason.ExplicitUserSave, memoryType: JudgeMemoryType.CodeFact,
            confidence: 0.4, rule: SoundRule()));
        Assert.Equal(JudgePersistAction.AutoCapture, outcome.Action);
    }

    // ---- Mapper: confidence bands --------------------------------------------

    [Theory]
    [InlineData(0.80, JudgePersistAction.AutoCapture)]
    [InlineData(0.95, JudgePersistAction.AutoCapture)]
    [InlineData(0.79, JudgePersistAction.Suggest)]
    [InlineData(0.55, JudgePersistAction.Suggest)]
    [InlineData(0.54, JudgePersistAction.Skip)]
    [InlineData(0.10, JudgePersistAction.Skip)]
    public void Mapper_ConfidenceBands(double confidence, JudgePersistAction expected)
    {
        var outcome = Map(Verdict(confidence: confidence, rule: SoundRule()));
        Assert.Equal(expected, outcome.Action);
    }

    // ---- Mapper: reason-forced skips -----------------------------------------

    [Theory]
    [InlineData(JudgeCaptureReason.SourceDocumentOnly)]
    [InlineData(JudgeCaptureReason.AssistantProse)]
    [InlineData(JudgeCaptureReason.CommandOutputOnly)]
    [InlineData(JudgeCaptureReason.LogOutputOnly)]
    [InlineData(JudgeCaptureReason.NotReusable)]
    public void Mapper_ReadOnlyReasons_SkipEvenAtHighConfidence(JudgeCaptureReason reason)
    {
        // A high-confidence verdict whose reason marks read-only source material is still skipped.
        var outcome = Map(Verdict(confidence: 0.99, reason: reason, rule: SoundRule()));
        Assert.Equal(JudgePersistAction.Skip, outcome.Action);
    }

    [Fact]
    public void Mapper_CodeFact_SkipsWithoutExplicitSave()
    {
        var outcome = Map(Verdict(memoryType: JudgeMemoryType.CodeFact, confidence: 0.95, rule: SoundRule()));
        Assert.Equal(JudgePersistAction.Skip, outcome.Action);
    }

    // ---- Mapper: reinforce / supersede ---------------------------------------

    [Fact]
    public void Mapper_Reinforce_MapsToReinforceWithTarget()
    {
        var outcome = Map(Verdict(decision: JudgeDecision.ReinforceExisting, target: 42, dedupeNotes: "same guidance"));
        Assert.Equal(JudgePersistAction.Reinforce, outcome.Action);
        Assert.Equal(42, outcome.TargetRuleId);
    }

    [Fact]
    public void Mapper_Supersede_MapsToSupersedeActive()
    {
        var outcome = Map(Verdict(decision: JudgeDecision.SupersedeExisting, target: 9, rule: SoundRule()));
        Assert.Equal(JudgePersistAction.Supersede, outcome.Action);
        Assert.Equal(9, outcome.TargetRuleId);
        Assert.Equal(RuleStatus.Active, outcome.Status);
    }

    // ---- Mapper: invalid → skip / downgrade, never a keyword fallback --------

    [Fact]
    public void Mapper_InvalidCaptureMissingCoreField_Skips()
    {
        var rule = SoundRule() with { Action = "" };
        var outcome = Map(Verdict(rule: rule));
        Assert.Equal(JudgePersistAction.Skip, outcome.Action);
        Assert.Contains("Invalid judge output", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapper_InvalidCaptureMissingBecause_DowngradesToSuggest()
    {
        var rule = SoundRule() with { Because = "" };
        var outcome = Map(Verdict(rule: rule));
        Assert.Equal(JudgePersistAction.Suggest, outcome.Action);
        Assert.Equal(RuleStatus.Pending, outcome.Status);
    }

    // ---- Mapper: category mapping --------------------------------------------

    [Theory]
    [InlineData(JudgeMemoryType.EngineeringLesson, RuleCategory.EngineeringLesson)]
    [InlineData(JudgeMemoryType.ReviewLesson, RuleCategory.EngineeringLesson)]
    [InlineData(JudgeMemoryType.DocBackedCorrection, RuleCategory.EngineeringLesson)]
    [InlineData(JudgeMemoryType.RepositoryConvention, RuleCategory.RepositoryConvention)]
    [InlineData(JudgeMemoryType.ToolWorkflowConvention, RuleCategory.RepositoryConvention)]
    [InlineData(JudgeMemoryType.UserPreference, RuleCategory.UserPreference)]
    [InlineData(JudgeMemoryType.CommunicationPreference, RuleCategory.CommunicationPreference)]
    public void Mapper_CategoryMapping(JudgeMemoryType type, RuleCategory expected)
    {
        var outcome = Map(Verdict(memoryType: type, rule: SoundRule()));
        Assert.Equal(expected, outcome.Category);
    }

    [Fact]
    public void Mapper_CarriesJudgeReasonAndDecisionNames()
    {
        var outcome = Map(Verdict(reason: JudgeCaptureReason.ReviewerCorrection, rule: SoundRule()));
        Assert.Equal("ReviewerCorrection", outcome.JudgeReason);
        Assert.Equal("Capture", outcome.JudgeDecision);
        Assert.Equal(CaptureReason.AcceptedReviewComment, outcome.DomainReason);
    }
}
