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

    // ---- Model defaults ---------------------------------------------------------

    [Fact]
    public void CaptureJudgeOutcome_Defaults_AreEmptyStringsNotSentinels()
    {
        var outcome = new CaptureJudgeOutcome { Action = JudgePersistAction.Skip };
        Assert.Equal(string.Empty, outcome.JudgeReason);
        Assert.Equal(string.Empty, outcome.JudgeDecision);
        Assert.Equal(string.Empty, outcome.Reason);
    }

    [Fact]
    public void CaptureJudgeInput_Source_DefaultsToEmpty()
    {
        Assert.Equal(string.Empty, new CaptureJudgeInput().Source);
    }

    // ---- Validator ------------------------------------------------------------

    [Fact]
    public void Validator_SoundCapture_IsValid()
    {
        var result = CaptureJudgeValidator.Validate(Verdict(rule: SoundRule()));
        Assert.True(result.IsValid);
        Assert.Equal(string.Empty, result.Reason);
        Assert.False(result.DowngradeToSuggest);
    }

    [Fact]
    public void CaptureJudgeValidation_Valid_IsThePassingSingleton()
    {
        Assert.True(CaptureJudgeValidation.Valid.IsValid);
        Assert.Equal(string.Empty, CaptureJudgeValidation.Valid.Reason);
        Assert.False(CaptureJudgeValidation.Valid.DowngradeToSuggest);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Validator_ConfidenceOutOfRange_IsInvalid(double confidence)
    {
        Assert.False(CaptureJudgeValidator.Validate(Verdict(confidence: confidence, rule: SoundRule())).IsValid);
    }

    // The confidence range is inclusive at both ends: exactly 0.0 and exactly 1.0 are valid.
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Validator_ConfidenceAtBoundary_IsValid(double confidence)
    {
        Assert.True(CaptureJudgeValidator.Validate(Verdict(confidence: confidence, rule: SoundRule())).IsValid);
    }

    [Fact]
    public void Validator_ConfidenceOutOfRange_ReasonNamesIt()
    {
        var result = CaptureJudgeValidator.Validate(Verdict(confidence: -0.1, rule: SoundRule()));
        Assert.Contains("confidence out of range", result.Reason, StringComparison.Ordinal);
    }

    // ---- IsMinimallyStorable / IsSound: null and boundary handling ------------

    [Fact]
    public void IsMinimallyStorable_NullRule_IsFalse()
    {
        Assert.False(CaptureJudgeValidator.IsMinimallyStorable(null));
    }

    [Fact]
    public void IsSound_NullRule_IsFalse()
    {
        Assert.False(CaptureJudgeValidator.IsSound(null));
    }

    [Fact]
    public void Validator_TitleAtExactMaxLength_IsValid()
    {
        var rule = SoundRule() with { Title = new string('x', CaptureJudgeValidator.TitleMaxLength) };
        Assert.True(CaptureJudgeValidator.Validate(Verdict(rule: rule)).IsValid);
    }

    [Fact]
    public void Validator_TitleOneOverMaxLength_IsInvalid()
    {
        var rule = SoundRule() with { Title = new string('x', CaptureJudgeValidator.TitleMaxLength + 1) };
        var result = CaptureJudgeValidator.Validate(Verdict(rule: rule));
        Assert.False(result.IsValid);
        Assert.Contains("title too long", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Condition")]
    [InlineData("Action")]
    [InlineData("Avoid")]
    [InlineData("Because")]
    [InlineData("Scope")]
    public void Validator_EachField_AtExactMaxLength_IsValid_ButOneOverIsInvalid(string field)
    {
        var atMax = new string('x', CaptureJudgeValidator.FieldMaxLength);
        var overMax = new string('x', CaptureJudgeValidator.FieldMaxLength + 1);

        Assert.True(CaptureJudgeValidator.Validate(Verdict(rule: WithField(SoundRule(), field, atMax))).IsValid);

        var result = CaptureJudgeValidator.Validate(Verdict(rule: WithField(SoundRule(), field, overMax)));
        Assert.False(result.IsValid);
        Assert.Contains("normalized rule field too long", result.Reason, StringComparison.Ordinal);
    }

    private static NormalizedRule WithField(NormalizedRule rule, string field, string value) => field switch
    {
        "Condition" => rule with { Condition = value },
        "Action" => rule with { Action = value },
        "Avoid" => rule with { Avoid = value },
        "Because" => rule with { Because = value },
        "Scope" => rule with { Scope = value },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    [Fact]
    public void Validator_TagCountAtExactMax_IsValid()
    {
        var rule = SoundRule() with { Tags = Enumerable.Range(0, CaptureJudgeValidator.MaxTags).Select(i => $"t{i}").ToList() };
        Assert.True(CaptureJudgeValidator.Validate(Verdict(rule: rule)).IsValid);
    }

    [Fact]
    public void Validator_TagCountOneOverMax_IsInvalid()
    {
        var rule = SoundRule() with { Tags = Enumerable.Range(0, CaptureJudgeValidator.MaxTags + 1).Select(i => $"t{i}").ToList() };
        var result = CaptureJudgeValidator.Validate(Verdict(rule: rule));
        Assert.False(result.IsValid);
        Assert.Contains("too many or over-long tags", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_TagAtExactMaxLength_IsValid_ButOneOverIsInvalid()
    {
        var okRule = SoundRule() with { Tags = [new string('t', CaptureJudgeValidator.TagMaxLength)] };
        Assert.True(CaptureJudgeValidator.Validate(Verdict(rule: okRule)).IsValid);

        var overRule = SoundRule() with { Tags = [new string('t', CaptureJudgeValidator.TagMaxLength + 1)] };
        Assert.False(CaptureJudgeValidator.Validate(Verdict(rule: overRule)).IsValid);
    }

    [Fact]
    public void Validator_CaptureMissingCondition_IsHardInvalid()
    {
        var rule = SoundRule() with { Condition = "" };
        var result = CaptureJudgeValidator.Validate(Verdict(rule: rule));
        Assert.False(result.IsValid);
        Assert.False(result.DowngradeToSuggest);
        Assert.Contains("capture is missing title/condition/action", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_CaptureMissingBecause_DowngradesToSuggest()
    {
        var rule = SoundRule() with { Because = "" };
        var result = CaptureJudgeValidator.Validate(Verdict(rule: rule));
        Assert.False(result.IsValid);
        Assert.True(result.DowngradeToSuggest);
        Assert.Contains("capture is missing because/scope", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_SkipMissingWhyNotSaved_IsInvalid()
    {
        var result = CaptureJudgeValidator.Validate(Verdict(decision: JudgeDecision.Skip));
        Assert.False(result.IsValid);
        Assert.Contains("skip is missing why_not_saved", result.Reason, StringComparison.Ordinal);
        Assert.True(CaptureJudgeValidator.Validate(
            Verdict(decision: JudgeDecision.Skip, whyNotSaved: "assistant prose")).IsValid);
    }

    [Fact]
    public void Validator_ReinforceRequiresTargetAndNotes()
    {
        var missingTarget = CaptureJudgeValidator.Validate(
            Verdict(decision: JudgeDecision.ReinforceExisting, dedupeNotes: "same rule"));
        Assert.False(missingTarget.IsValid);
        Assert.Contains("reinforce is missing target_existing_rule_id", missingTarget.Reason, StringComparison.Ordinal);

        var missingNotes = CaptureJudgeValidator.Validate(
            Verdict(decision: JudgeDecision.ReinforceExisting, target: 7));
        Assert.False(missingNotes.IsValid);
        Assert.Contains("reinforce is missing dedupe_notes", missingNotes.Reason, StringComparison.Ordinal);

        Assert.True(CaptureJudgeValidator.Validate(
            Verdict(decision: JudgeDecision.ReinforceExisting, target: 7, dedupeNotes: "same rule")).IsValid);
    }

    [Fact]
    public void Validator_SupersedeRequiresTargetAndSoundRule()
    {
        var result = CaptureJudgeValidator.Validate(Verdict(decision: JudgeDecision.SupersedeExisting, rule: SoundRule()));
        Assert.False(result.IsValid);
        Assert.Contains("supersede is missing target_existing_rule_id", result.Reason, StringComparison.Ordinal);
        Assert.True(CaptureJudgeValidator.Validate(
            Verdict(decision: JudgeDecision.SupersedeExisting, target: 3, rule: SoundRule())).IsValid);
    }

    // A target is present but the rule is unsound (missing rationale/scope) — still invalid,
    // proving the target check and the soundness check are both enforced, not just the target.
    [Fact]
    public void Validator_Supersede_TargetPresentButUnsoundRule_IsInvalid()
    {
        var rule = SoundRule() with { Because = "" };
        var result = CaptureJudgeValidator.Validate(
            Verdict(decision: JudgeDecision.SupersedeExisting, target: 3, rule: rule));
        Assert.False(result.IsValid);
        Assert.Contains("supersede is missing a sound normalized_rule", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_SuggestCapture_MinimallyStorableRule_IsValid()
    {
        // A suggestion only needs the minimal fields, not the full soundness bar.
        var rule = SoundRule() with { Because = "", Scope = "" };
        Assert.True(CaptureJudgeValidator.Validate(
            Verdict(decision: JudgeDecision.SuggestCapture, rule: rule)).IsValid);
    }

    [Fact]
    public void Validator_SuggestCapture_NotMinimallyStorable_IsInvalid()
    {
        var rule = SoundRule() with { Action = "" };
        var result = CaptureJudgeValidator.Validate(Verdict(decision: JudgeDecision.SuggestCapture, rule: rule));
        Assert.False(result.IsValid);
        Assert.Contains("suggestion is missing title/condition/action", result.Reason, StringComparison.Ordinal);
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
        var result = CaptureJudgeValidator.Validate(Verdict(rule: rule), input);
        Assert.False(result.IsValid);
        Assert.Contains("normalized rule echoes raw turn text", result.Reason, StringComparison.Ordinal);
    }

    // The echo check is exact (modulo trim/case), not "any similarity" — a rule that merely
    // overlaps with the assistant summary is not raw-text-echo and stays valid.
    [Fact]
    public void Validator_RuleSimilarButNotEqualToAssistantSummary_IsValid()
    {
        var input = new CaptureJudgeInput { AssistantSummary = "We changed the button in the registration modal." };
        var rule = SoundRule(); // unrelated action/title text
        Assert.True(CaptureJudgeValidator.Validate(Verdict(rule: rule), input).IsValid);
    }

    // No assistant summary on the input means the echo guard can never fire.
    [Fact]
    public void Validator_NoAssistantSummary_EchoGuardNeverFires()
    {
        var input = new CaptureJudgeInput { AssistantSummary = null };
        Assert.True(CaptureJudgeValidator.Validate(Verdict(rule: SoundRule()), input).IsValid);
    }

    // The title alone matching the assistant summary verbatim is enough to flag the echo,
    // even when the action text is unrelated.
    [Fact]
    public void Validator_TitleEchoesAssistantSummary_IsInvalid()
    {
        var raw = "We flattened the nested if blocks and dropped the else branch entirely.";
        var rule = SoundRule() with { Title = raw };
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

    [Fact]
    public void Mapper_ExplicitSave_UnsoundRule_Skips()
    {
        var rule = SoundRule() with { Because = "" };
        var verdict = Verdict(reason: JudgeCaptureReason.ExplicitUserSave, rule: rule);
        // Bypass Validate() (which would already reject this) to exercise the mapper's own
        // IsSound gate on the explicit-save branch directly.
        var outcome = CaptureJudgeDecisionMapper.Map(verdict, CaptureJudgeValidation.Valid);
        Assert.Equal(JudgePersistAction.Skip, outcome.Action);
        Assert.Equal("Explicit save without a storable rule.", outcome.Reason);
    }

    [Fact]
    public void Mapper_ExplicitSave_SoundRule_ReasonIsExplicitUserSave()
    {
        var outcome = Map(Verdict(reason: JudgeCaptureReason.ExplicitUserSave, rule: SoundRule()));
        Assert.Equal("Explicit user save.", outcome.Reason);
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

    [Fact]
    public void Mapper_ConfidenceBands_ReasonTextNamesTheBand()
    {
        Assert.Equal("Captured on judge confidence.", Map(Verdict(confidence: 0.9, rule: SoundRule())).Reason);
        Assert.Equal("Suggested for review on judge confidence.", Map(Verdict(confidence: 0.6, rule: SoundRule())).Reason);
        Assert.Equal("Below the capture confidence threshold.", Map(Verdict(confidence: 0.1, rule: SoundRule())).Reason);
    }

    [Fact]
    public void Mapper_Skip_UsesJudgesWhyNotSaved_NotTheDefaultText()
    {
        var outcome = Map(Verdict(decision: JudgeDecision.Skip, whyNotSaved: "duplicate of an existing convention"));
        Assert.Equal("duplicate of an existing convention", outcome.Reason);
    }

    [Fact]
    public void Mapper_Skip_NullWhyNotSaved_FallsBackToDefaultText()
    {
        // Bypass Validate() (Skip normally requires WhyNotSaved) to exercise the mapper's own
        // null-coalescing fallback directly.
        var verdict = Verdict(decision: JudgeDecision.Skip, whyNotSaved: null);
        var outcome = CaptureJudgeDecisionMapper.Map(verdict, CaptureJudgeValidation.Valid);
        Assert.Equal("Not memory-worthy.", outcome.Reason);
    }

    // ---- Mapper: reason-forced skips -----------------------------------------

    [Theory]
    [InlineData(JudgeCaptureReason.SourceDocumentOnly)]
    [InlineData(JudgeCaptureReason.AssistantProse)]
    [InlineData(JudgeCaptureReason.CommandOutputOnly)]
    [InlineData(JudgeCaptureReason.LogOutputOnly)]
    [InlineData(JudgeCaptureReason.NotMemory)]
    [InlineData(JudgeCaptureReason.NotReusable)]
    [InlineData(JudgeCaptureReason.Ambiguous)]
    public void Mapper_ReadOnlyReasons_SkipEvenAtHighConfidence(JudgeCaptureReason reason)
    {
        // A high-confidence verdict whose reason marks read-only source material is still skipped.
        var outcome = Map(Verdict(confidence: 0.99, reason: reason, rule: SoundRule()));
        Assert.Equal(JudgePersistAction.Skip, outcome.Action);
        Assert.Equal($"Not stored: {reason}.", outcome.Reason);
    }

    [Fact]
    public void Mapper_CodeFact_SkipsWithoutExplicitSave()
    {
        var outcome = Map(Verdict(memoryType: JudgeMemoryType.CodeFact, confidence: 0.95, rule: SoundRule()));
        Assert.Equal(JudgePersistAction.Skip, outcome.Action);
        Assert.Equal("Code fact, recoverable from the repository.", outcome.Reason);
    }

    // ---- Mapper: self-identified friction never auto-captures ----------------

    [Fact]
    public void Mapper_SelfIdentifiedFriction_HighConfidence_CapsAtSuggestNeverCaptures()
    {
        // A reflective finding is the model's own self-assessment, not an observed external
        // signal — even at a confidence that would normally AutoCapture, it stays a suggestion.
        var outcome = Map(Verdict(
            reason: JudgeCaptureReason.SelfIdentifiedFriction, confidence: 0.99, rule: SoundRule()));
        Assert.Equal(JudgePersistAction.Suggest, outcome.Action);
        Assert.Equal(RuleStatus.Pending, outcome.Status);
        Assert.Equal("Self-identified friction — parked for review, not auto-captured.", outcome.Reason);
    }

    [Fact]
    public void Mapper_SelfIdentifiedFriction_BelowSuggestThreshold_Skips()
    {
        var outcome = Map(Verdict(
            reason: JudgeCaptureReason.SelfIdentifiedFriction, confidence: 0.2, rule: SoundRule()));
        Assert.Equal(JudgePersistAction.Skip, outcome.Action);
        Assert.Equal("Self-identified friction below the suggestion confidence threshold.", outcome.Reason);
    }

    [Fact]
    public void Mapper_SelfIdentifiedFriction_AtSuggestThreshold_Suggests()
    {
        var outcome = Map(Verdict(
            reason: JudgeCaptureReason.SelfIdentifiedFriction,
            confidence: CaptureJudgeDecisionMapper.SuggestThreshold,
            rule: SoundRule()));
        Assert.Equal(JudgePersistAction.Suggest, outcome.Action);
    }

    [Fact]
    public void Mapper_SelfIdentifiedFriction_WithReinforceDecision_StillReinforces()
    {
        // The judge's explicit decision (recognizing a repeat) still takes precedence over the
        // reason's own confidence cap — reinforcement is a distinct action from a fresh capture.
        var outcome = Map(Verdict(
            decision: JudgeDecision.ReinforceExisting,
            reason: JudgeCaptureReason.SelfIdentifiedFriction,
            target: 7, dedupeNotes: "same friction point as rule #7"));
        Assert.Equal(JudgePersistAction.Reinforce, outcome.Action);
        Assert.Equal(7, outcome.TargetRuleId);
    }

    // ---- Mapper: reinforce / supersede ---------------------------------------

    [Fact]
    public void Mapper_Reinforce_MapsToReinforceWithTarget()
    {
        var outcome = Map(Verdict(decision: JudgeDecision.ReinforceExisting, target: 42, dedupeNotes: "same guidance"));
        Assert.Equal(JudgePersistAction.Reinforce, outcome.Action);
        Assert.Equal(42, outcome.TargetRuleId);
        Assert.Equal("Reinforced existing rule #42.", outcome.Reason);
    }

    [Fact]
    public void Mapper_Supersede_MapsToSupersedeActive()
    {
        var outcome = Map(Verdict(decision: JudgeDecision.SupersedeExisting, target: 9, rule: SoundRule()));
        Assert.Equal(JudgePersistAction.Supersede, outcome.Action);
        Assert.Equal(9, outcome.TargetRuleId);
        Assert.Equal(RuleStatus.Active, outcome.Status);
        Assert.Equal("Supersedes rule #9.", outcome.Reason);
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
        Assert.Equal("Downgraded to suggestion: capture is missing because/scope.", outcome.Reason);
    }

    // The downgrade requires BOTH validation.DowngradeToSuggest AND a minimally storable rule —
    // either alone must still skip, proving the mapper doesn't just trust the validation flag.
    [Fact]
    public void Mapper_DowngradeFlagSet_ButRuleNotMinimallyStorable_StillSkips()
    {
        var validation = CaptureJudgeValidation.Invalid("capture is missing because/scope", downgradeToSuggest: true);
        var outcome = CaptureJudgeDecisionMapper.Map(Verdict(rule: null), validation);
        Assert.Equal(JudgePersistAction.Skip, outcome.Action);
        Assert.Contains("Invalid judge output", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapper_MinimallyStorableRule_ButDowngradeFlagNotSet_StillSkips()
    {
        var validation = CaptureJudgeValidation.Invalid("some other reason", downgradeToSuggest: false);
        var outcome = CaptureJudgeDecisionMapper.Map(Verdict(rule: SoundRule()), validation);
        Assert.Equal(JudgePersistAction.Skip, outcome.Action);
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

    [Theory]
    [InlineData(JudgeCaptureReason.ExplicitUserSave, CaptureReason.ManualFeedback)]
    [InlineData(JudgeCaptureReason.ObservedAgentFailure, CaptureReason.ObservedAgentFailure)]
    [InlineData(JudgeCaptureReason.ReviewerCorrection, CaptureReason.AcceptedReviewComment)]
    [InlineData(JudgeCaptureReason.UserCorrection, CaptureReason.UserCorrection)]
    [InlineData(JudgeCaptureReason.RepeatedMistake, CaptureReason.RepeatedCorrection)]
    [InlineData(JudgeCaptureReason.UserPreference, CaptureReason.ExplicitUserPreference)]
    [InlineData(JudgeCaptureReason.DocBackedCorrection, CaptureReason.ObservedAgentFailure)]
    [InlineData(JudgeCaptureReason.NotMemory, CaptureReason.None)]
    [InlineData(JudgeCaptureReason.SelfIdentifiedFriction, CaptureReason.SelfIdentifiedFriction)]
    public void Mapper_DomainReasonMapping(JudgeCaptureReason reason, CaptureReason expected)
    {
        // DomainReason is computed for every outcome regardless of branch, so a plain
        // high-confidence capture is enough to observe the mapping for any reason value.
        var outcome = Map(Verdict(reason: reason, confidence: 0.9, rule: SoundRule()));
        Assert.Equal(expected, outcome.DomainReason);
    }

    [Fact]
    public void Mapper_CarriesJudgeReasonAndDecisionNames()
    {
        var outcome = Map(Verdict(reason: JudgeCaptureReason.ReviewerCorrection, rule: SoundRule()));
        Assert.Equal("ReviewerCorrection", outcome.JudgeReason);
        Assert.Equal("Capture", outcome.JudgeDecision);
        Assert.Equal(CaptureReason.AcceptedReviewComment, outcome.DomainReason);
    }

    // ---- Always-apply classification ------------------------------------------

    [Fact] // The judge's explicit always_apply flag makes the outcome standing.
    public void Mapper_AlwaysApplyFlag_MarksOutcomeStanding()
    {
        var rule = SoundRule() with { AlwaysApply = true };
        Assert.True(Map(Verdict(rule: rule)).AlwaysApply);
    }

    [Theory] // A preference is standing by nature, even without the flag.
    [InlineData(JudgeMemoryType.UserPreference)]
    [InlineData(JudgeMemoryType.CommunicationPreference)]
    public void Mapper_Preference_IsAlwaysApply(JudgeMemoryType type)
    {
        var outcome = Map(Verdict(memoryType: type, reason: JudgeCaptureReason.UserPreference, rule: SoundRule()));
        Assert.True(outcome.AlwaysApply);
    }

    [Fact] // An ordinary engineering lesson is not standing unless flagged.
    public void Mapper_OrdinaryLesson_IsNotAlwaysApply()
    {
        Assert.False(Map(Verdict(rule: SoundRule())).AlwaysApply);
    }
}
