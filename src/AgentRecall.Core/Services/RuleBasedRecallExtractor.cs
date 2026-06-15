using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Extraction;
using AgentRecall.Core.Feedback;

namespace AgentRecall.Core.Services;

/// <summary>
/// A deterministic, rule-based <see cref="IRecallExtractor"/>. It derives the
/// structured fields with <see cref="StructuredRuleExtractor"/>, maps them onto a
/// <see cref="RecallRule"/>, and sanitises the result with
/// <see cref="IRecallRuleQualityValidator"/> — no LLM involved.
/// </summary>
public sealed class RuleBasedRecallExtractor : IRecallExtractor
{
    /// <summary>Confidence assigned to rules produced without an LLM.</summary>
    public const double DefaultConfidence = 0.5;

    private readonly IRecallRuleQualityValidator _validator;

    public RuleBasedRecallExtractor(IRecallRuleQualityValidator validator)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public RecallRule Extract(FeedbackInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var extracted = StructuredRuleExtractor.Extract(input);

        var rule = new RecallRule
        {
            Version = 1,
            Status = RuleStatus.Pending,
            Trigger = extracted.Trigger,
            RuleText = extracted.Rule,
            Mistake = extracted.DoNot,
            // Reason goes in TechnicalContext — and is never the scope value.
            TechnicalContext = extracted.Reason,
            Tags = extracted.Tags,
            Confidence = DefaultConfidence,
            ScopeLevel = input.ScopeLevel,
            ScopeValue = (input.ScopeValue ?? string.Empty).Trim(),
        };

        return _validator.Validate(rule).Rule;
    }
}
