using AgentRecall.Core.Domain;
using AgentRecall.Core.Extraction;
using AgentRecall.Core.Feedback;
using Xunit;

namespace AgentRecall.Tests;

public class ExtractionQualityTests
{
    // ---- Structured extraction ------------------------------------------------

    [Fact]
    public void Extract_NormalizesTriggerIntoReadableCondition()
    {
        var extracted = StructuredRuleExtractor.Extract(new FeedbackInput
        {
            Task = "write Moq tests for service X",
            Feedback = "Use argument matchers consistently.",
        });

        // Readable condition, not "When write Moq tests for service X:".
        Assert.Equal("When writing Moq tests for service X", extracted.Trigger);
    }

    [Fact]
    public void Extract_WithoutBadOutputOrProhibition_LeavesDoNotEmpty()
    {
        var extracted = StructuredRuleExtractor.Extract(new FeedbackInput
        {
            Task = "writing SQL",
            Feedback = "Use parameterized queries.",
        });

        Assert.False(string.IsNullOrEmpty(extracted.Do));
        Assert.Equal(string.Empty, extracted.DoNot);
    }

    [Fact]
    public void Extract_DerivesDistinctDoAndDoNot()
    {
        var extracted = StructuredRuleExtractor.Extract(new FeedbackInput
        {
            Task = "writing SQL",
            Feedback = "Use parameterized queries. Never concatenate user input into SQL.",
        });

        Assert.False(string.IsNullOrEmpty(extracted.DoNot));
        Assert.NotEqual(extracted.Do, extracted.DoNot);
        Assert.StartsWith("Never", extracted.DoNot);
    }

    [Fact]
    public void Extract_DoesNotShredCodeWithDots()
    {
        var extracted = StructuredRuleExtractor.Extract(new FeedbackInput
        {
            Task = "writing Moq tests",
            Feedback = "Always use It.IsAny<T>() for unspecified Moq arguments.",
        });

        // The period inside It.IsAny<T>() must not truncate the rule.
        Assert.Contains("It.IsAny<T>()", extracted.Do, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_PullsReasonFromRationale_NotScope()
    {
        var extracted = StructuredRuleExtractor.Extract(new FeedbackInput
        {
            Task = "writing SQL",
            Feedback = "Use parameterized queries to avoid SQL injection.",
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "my-repo",
        });

        Assert.Contains("injection", extracted.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("my-repo", extracted.Reason);
    }

    [Fact]
    public void Extract_NoRationale_LeavesReasonEmpty_NotScope()
    {
        var extracted = StructuredRuleExtractor.Extract(new FeedbackInput
        {
            Task = "formatting",
            Feedback = "Use tabs for indentation.",
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "my-repo",
        });

        Assert.Equal(string.Empty, extracted.Reason);
    }

    // ---- Quality validator ----------------------------------------------------

    private static RecallRule Rule(
        string ruleText = "Use parameterized queries.",
        string trigger = "When writing SQL",
        string mistake = "",
        string technicalContext = "",
        string scopeValue = "",
        ScopeLevel scopeLevel = ScopeLevel.Global,
        double confidence = 0.5) => new()
    {
        RuleText = ruleText, Trigger = trigger, Mistake = mistake,
        TechnicalContext = technicalContext, ScopeValue = scopeValue,
        ScopeLevel = scopeLevel, Confidence = confidence,
    };

    private static readonly RecallRuleQualityValidator Validator = new();

    [Fact]
    public void Validate_BlanksReasonThatEqualsScopeValue()
    {
        var rule = Rule(technicalContext: "my-repo", scopeValue: "my-repo", scopeLevel: ScopeLevel.Repository);

        var result = Validator.Validate(rule);

        Assert.Equal(string.Empty, result.Rule.TechnicalContext);
        Assert.Contains(result.Issues, i => i.Field == "reason");
    }

    [Fact]
    public void Validate_BlanksDoNotEquivalentToDo()
    {
        var rule = Rule(ruleText: "Use parameterized queries.", mistake: "Use parameterized queries.");

        var result = Validator.Validate(rule);

        Assert.Equal(string.Empty, result.Rule.Mistake);
        Assert.Contains(result.Issues, i => i.Field == "do_not");
    }

    [Fact]
    public void Validate_BlanksProhibitiveDoNotEquivalentToDo()
    {
        // The do_not is genuinely prohibitive (so it survives the negativity check) but
        // says the same thing as the do — that redundant structure is blanked.
        var rule = Rule(
            ruleText: "Never concatenate user input into SQL.",
            mistake: "Do not concatenate user input into SQL.");

        var result = Validator.Validate(rule);

        Assert.Equal(string.Empty, result.Rule.Mistake);
        Assert.Contains(result.Issues, i => i.Field == "do_not" && i.Message.Contains("equivalent"));
    }

    [Fact]
    public void Validate_BlanksNonProhibitiveDoNot()
    {
        // A "do not" that isn't actually negative is not real structure.
        var rule = Rule(ruleText: "Use parameterized queries.", mistake: "Concatenate strings freely.");

        var result = Validator.Validate(rule);

        Assert.Equal(string.Empty, result.Rule.Mistake);
        Assert.Contains(result.Issues, i => i.Field == "do_not");
    }

    [Fact]
    public void Validate_KeepsGenuineProhibition()
    {
        var rule = Rule(ruleText: "Use parameterized queries.", mistake: "Never concatenate user input into SQL.");

        var result = Validator.Validate(rule);

        Assert.True(result.IsValid);
        Assert.Equal("Never concatenate user input into SQL.", result.Rule.Mistake);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Validate_EmptyRuleText_IsInvalidAndLowConfidence()
    {
        var rule = Rule(ruleText: "", confidence: 0.9);

        var result = Validator.Validate(rule);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Field == "rule");
        Assert.True(result.Rule.Confidence <= RecallRuleQualityValidator.InvalidConfidenceCeiling);
    }

    [Fact]
    public void Validate_EmptyTrigger_IsInvalid()
    {
        var rule = Rule(trigger: "", confidence: 0.9);

        var result = Validator.Validate(rule);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Field == "trigger");
    }
}
