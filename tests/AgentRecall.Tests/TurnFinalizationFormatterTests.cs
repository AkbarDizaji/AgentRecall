using AgentRecall.Core.Domain;
using AgentRecall.Core.Finalization;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Focused, pure unit tests for <see cref="TurnFinalizationFormatter"/>: hand-built
/// <see cref="TurnFinalizationResult"/> instances, no database. Written to kill Stryker
/// mutants surfaced against TurnFinalizationFormatter.cs (see the mutant report used to
/// derive these cases for line references).
/// </summary>
public class TurnFinalizationFormatterTests
{
    private static FinalizedLesson Lesson(
        int ruleId = 1,
        RuleCategory category = RuleCategory.EngineeringLesson,
        string text = "Some lesson text.",
        bool alwaysApply = false,
        string? note = null) => new()
    {
        RuleId = ruleId,
        Category = category,
        Text = text,
        ScopeLabel = "Repository:project",
        AlwaysApply = alwaysApply,
        Note = note,
    };

    private static SkippedLesson Skip(string reason = "Not novel enough.", int? duplicateOfRuleId = null) => new()
    {
        Reason = reason,
        DuplicateOfRuleId = duplicateOfRuleId,
    };

    // ----- RenderText: null guard (L25) -----

    [Fact]
    public void RenderText_NullResult_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TurnFinalizationFormatter.RenderText(null!));
    }

    // ----- RenderText: empty result (L29) -----

    [Fact]
    public void RenderText_EmptyResult_ReturnsNoLessonsFound()
    {
        var result = new TurnFinalizationResult();
        Assert.Equal("No lessons found.", TurnFinalizationFormatter.RenderText(result));
    }

    // ----- RenderText: header (L33) -----

    [Fact]
    public void RenderText_NonEmptyResult_StartsWithFinalizedHeader()
    {
        var result = new TurnFinalizationResult { Captured = [Lesson()] };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.StartsWith("AgentRecall finalized turn.", text, StringComparison.Ordinal);
    }

    // ----- RenderText: decision-source logical condition (L35) -----

    [Fact]
    public void RenderText_JudgeSourceButNoDecision_OmitsDecisionSection()
    {
        // First operand true, second false: a || would wrongly print the section.
        var result = new TurnFinalizationResult
        {
            Captured = [Lesson()],
            DecisionSource = TurnFinalizationFormatter.JudgeDecisionSource,
            Decision = null,
        };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.DoesNotContain("Decision source", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderText_DecisionButNotJudgeSource_OmitsDecisionSection()
    {
        // First operand false, second true: a || would wrongly print the section.
        var result = new TurnFinalizationResult
        {
            Captured = [Lesson()],
            DecisionSource = "None",
            Decision = "Capture",
        };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.DoesNotContain("Decision source", text, StringComparison.Ordinal);
    }

    // ----- RenderText: decision line content (L38) -----

    [Fact]
    public void RenderText_JudgeDecision_RendersDecisionLine()
    {
        var result = new TurnFinalizationResult
        {
            Captured = [Lesson()],
            DecisionSource = TurnFinalizationFormatter.JudgeDecisionSource,
            Decision = "Capture",
        };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.Contains("Decision source: Semantic capture judge", text, StringComparison.Ordinal);
        Assert.Contains("Decision: Capture", text, StringComparison.Ordinal);
    }

    // ----- RenderText: judge reason with confidence (L42, L43) -----

    [Fact]
    public void RenderText_JudgeReasonWithConfidence_IncludesFormattedConfidence()
    {
        var result = new TurnFinalizationResult
        {
            Captured = [Lesson()],
            DecisionSource = TurnFinalizationFormatter.JudgeDecisionSource,
            Decision = "Capture",
            JudgeReason = "NovelLesson",
            JudgeConfidence = 0.5,
        };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.Contains("(reason: NovelLesson, confidence: 0.50)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderText_JudgeReasonWithoutConfidence_OmitsConfidenceSuffix()
    {
        var result = new TurnFinalizationResult
        {
            Captured = [Lesson()],
            DecisionSource = TurnFinalizationFormatter.JudgeDecisionSource,
            Decision = "Capture",
            JudgeReason = "NovelLesson",
            JudgeConfidence = null,
        };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.Contains("(reason: NovelLesson)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("confidence", text, StringComparison.Ordinal);
    }

    // ----- RenderText: Captured section boundary (L48) -----

    [Fact]
    public void RenderText_NoCaptured_OmitsCapturedSection()
    {
        var result = new TurnFinalizationResult { Skipped = [Skip()] };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.DoesNotContain("Captured:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderText_OneCaptured_IncludesCapturedSection()
    {
        var result = new TurnFinalizationResult { Captured = [Lesson(ruleId: 7)] };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.Contains("Captured:", text, StringComparison.Ordinal);
        Assert.Contains("#7", text, StringComparison.Ordinal);
    }

    // ----- RenderText: AlwaysApply standing marker (L53) -----

    [Fact]
    public void RenderText_AlwaysApplyLesson_IncludesStandingMarker()
    {
        var result = new TurnFinalizationResult { Captured = [Lesson(alwaysApply: true)] };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.Contains("[standing — applies every turn]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderText_NonStandingLesson_OmitsStandingMarker()
    {
        var result = new TurnFinalizationResult { Captured = [Lesson(alwaysApply: false)] };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.DoesNotContain("[standing", text, StringComparison.Ordinal);
    }

    // ----- RenderText: Skipped section boundary (L58) -----

    [Fact]
    public void RenderText_NoSkipped_OmitsSkippedSection()
    {
        var result = new TurnFinalizationResult { Captured = [Lesson()] };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.DoesNotContain("Skipped:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderText_OneSkipped_IncludesSkippedSectionWithReason()
    {
        var result = new TurnFinalizationResult { Skipped = [Skip(reason: "Too vague to store.")] };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.Contains("Skipped:", text, StringComparison.Ordinal);
        Assert.Contains("- Too vague to store.", text, StringComparison.Ordinal);
    }

    // ----- RenderText: Suggested section boundary (L67) -----

    [Fact]
    public void RenderText_NoSuggested_OmitsSuggestedSection()
    {
        var result = new TurnFinalizationResult { Captured = [Lesson()] };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.DoesNotContain("Suggested:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderText_OneSuggested_IncludesSuggestedSection()
    {
        var result = new TurnFinalizationResult { Suggested = [Lesson(ruleId: 21)] };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.Contains("Suggested:", text, StringComparison.Ordinal);
        Assert.Contains("#21 Pending rule:", text, StringComparison.Ordinal);
    }

    // ----- RenderText: Suggested note ternary (L72) -----

    [Fact]
    public void RenderText_SuggestedWithoutNote_OmitsParenthetical()
    {
        var result = new TurnFinalizationResult { Suggested = [Lesson(ruleId: 22, note: null)] };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.DoesNotContain("(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderText_SuggestedWithWhitespaceNote_OmitsParenthetical()
    {
        var result = new TurnFinalizationResult { Suggested = [Lesson(ruleId: 23, note: "   ")] };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.DoesNotContain("(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderText_SuggestedWithNote_IncludesParenthetical()
    {
        var result = new TurnFinalizationResult { Suggested = [Lesson(ruleId: 24, note: "low confidence")] };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.Contains("(low confidence)", text, StringComparison.Ordinal);
    }

    // ----- RenderText: approve/archive hint line (L75) -----

    [Fact]
    public void RenderText_Suggested_IncludesApproveArchiveHint()
    {
        var result = new TurnFinalizationResult { Suggested = [Lesson(ruleId: 33)] };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.Contains(
            "Run `agentrecall rules approve 33` to remember it, or `agentrecall rules archive 33` to ignore it.",
            text,
            StringComparison.Ordinal);
    }

    // ----- RenderText: Errors section boundary + negation (L79, L81, L84) -----

    [Fact]
    public void RenderText_NoErrors_OmitsErrorsSection()
    {
        var result = new TurnFinalizationResult { Captured = [Lesson()] };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.DoesNotContain("Errors:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderText_OneError_IncludesErrorsSectionWithMessage()
    {
        var result = new TurnFinalizationResult { Errors = ["Database unavailable."] };
        var text = TurnFinalizationFormatter.RenderText(result);
        Assert.Contains("Errors:", text, StringComparison.Ordinal);
        Assert.Contains("- Database unavailable.", text, StringComparison.Ordinal);
    }

    // ----- CategoryLabel (L133, L135, L136) -----

    [Fact]
    public void CategoryLabel_RepositoryConvention_ReturnsRepositoryRule()
    {
        Assert.Equal("Repository rule", TurnFinalizationFormatter.CategoryLabel(RuleCategory.RepositoryConvention));
    }

    [Fact]
    public void CategoryLabel_EngineeringLesson_ReturnsEngineeringLesson()
    {
        Assert.Equal("Engineering lesson", TurnFinalizationFormatter.CategoryLabel(RuleCategory.EngineeringLesson));
    }

    [Fact]
    public void CategoryLabel_CodeFact_ReturnsCodeFact()
    {
        Assert.Equal("Code fact", TurnFinalizationFormatter.CategoryLabel(RuleCategory.CodeFact));
    }

    [Fact]
    public void CategoryLabel_Unknown_ReturnsDefaultRuleLabel()
    {
        Assert.Equal("Rule", TurnFinalizationFormatter.CategoryLabel(RuleCategory.Unknown));
    }

    // ----- SummaryLine: null/empty short-circuit (L97, L98-100) -----

    [Fact]
    public void SummaryLine_NullResult_ReturnsNoFinalization()
    {
        // If `||` were mutated to `&&`, evaluating result.IsEmpty on a null result would throw.
        Assert.Equal(TurnFinalizationFormatter.NoFinalization, TurnFinalizationFormatter.SummaryLine(null));
    }

    [Fact]
    public void SummaryLine_NonNullEmptyResult_ReturnsNoFinalization()
    {
        var result = new TurnFinalizationResult();
        Assert.Equal(TurnFinalizationFormatter.NoFinalization, TurnFinalizationFormatter.SummaryLine(result));
    }

    // ----- SummaryLine: Captured extra-count suffix (L105) -----

    [Fact]
    public void SummaryLine_SingleCaptured_OmitsMoreSuffix()
    {
        var result = new TurnFinalizationResult { Captured = [Lesson(ruleId: 5, text: "Do the thing.")] };
        var summary = TurnFinalizationFormatter.SummaryLine(result);
        Assert.DoesNotContain("more", summary, StringComparison.Ordinal);
        Assert.Equal("AgentRecall captured rule #5: Do the thing.", summary);
    }

    [Fact]
    public void SummaryLine_MultipleCaptured_IncludesMoreSuffix()
    {
        var result = new TurnFinalizationResult
        {
            Captured =
            [
                Lesson(ruleId: 5, text: "Do the thing."),
                Lesson(ruleId: 6, text: "Do another thing."),
            ],
        };
        var summary = TurnFinalizationFormatter.SummaryLine(result);
        Assert.Contains("(+1 more)", summary, StringComparison.Ordinal);
    }

    // ----- SummaryLine: Suggested line content (L112) -----

    [Fact]
    public void SummaryLine_Suggested_RendersApproveHint()
    {
        var result = new TurnFinalizationResult { Suggested = [Lesson(ruleId: 40, text: "Prefer X over Y.")] };
        var summary = TurnFinalizationFormatter.SummaryLine(result);
        Assert.Equal(
            "AgentRecall suggested pending rule #40: Prefer X over Y. Run `agentrecall rules approve 40` to remember it.",
            summary);
    }

    // ----- SummaryLine: reinforced-duplicate predicate (L116, L119) -----

    [Fact]
    public void SummaryLine_SkippedWithDuplicateAmongNonDuplicates_ReportsReinforcedRule()
    {
        var result = new TurnFinalizationResult
        {
            Skipped =
            [
                Skip(reason: "Too vague.", duplicateOfRuleId: null),
                Skip(reason: "Duplicate of rule #9.", duplicateOfRuleId: 9),
            ],
        };
        var summary = TurnFinalizationFormatter.SummaryLine(result);
        Assert.Equal("AgentRecall reinforced existing rule #9 (no new rule).", summary);
    }

    // ----- SummaryLine: Skipped-count boundary (L122) -----

    [Fact]
    public void SummaryLine_ErrorsOnlyNoSkipped_ReturnsNoFinalization()
    {
        // Reaches the Skipped.Count > 0 check with an empty Skipped list; a >= 0 mutant
        // would index into the empty list and throw.
        var result = new TurnFinalizationResult { Errors = ["boom"] };
        Assert.Equal(TurnFinalizationFormatter.NoFinalization, TurnFinalizationFormatter.SummaryLine(result));
    }

    [Fact]
    public void SummaryLine_SkippedWithoutDuplicate_ReportsSkipReason()
    {
        var result = new TurnFinalizationResult { Skipped = [Skip(reason: "Not durable enough.")] };
        var summary = TurnFinalizationFormatter.SummaryLine(result);
        Assert.Equal("AgentRecall skipped capture: Not durable enough.", summary);
    }

    // ----- SummaryLine: Summarize() text handling (L142, L143) -----

    [Fact]
    public void SummaryLine_NullLessonText_ProducesEmptySummary()
    {
        var result = new TurnFinalizationResult { Captured = [Lesson(ruleId: 8, text: null!)] };
        var summary = TurnFinalizationFormatter.SummaryLine(result);
        Assert.Equal("AgentRecall captured rule #8: .", summary);
    }

    [Fact]
    public void SummaryLine_TextExactly100Chars_IsNotTruncated()
    {
        var text = new string('a', 100);
        var result = new TurnFinalizationResult { Captured = [Lesson(ruleId: 9, text: text)] };
        var summary = TurnFinalizationFormatter.SummaryLine(result);
        Assert.Equal($"AgentRecall captured rule #9: {text}.", summary);
        Assert.DoesNotContain("…", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryLine_TextOver100Chars_IsTruncatedWithEllipsis()
    {
        var text = new string('a', 150);
        var result = new TurnFinalizationResult { Captured = [Lesson(ruleId: 10, text: text)] };
        var summary = TurnFinalizationFormatter.SummaryLine(result);
        var expected = $"AgentRecall captured rule #10: {new string('a', 99)}….";
        Assert.Equal(expected, summary);
    }
}
