using System.Text;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;

namespace AgentRecall.Core.Services;

/// <summary>
/// A deterministic, rule-based <see cref="IRecallExtractor"/>. It assembles a
/// rule from the feedback fields using simple templating — no LLM involved.
/// </summary>
public sealed class RuleBasedRecallExtractor : IRecallExtractor
{
    /// <summary>Confidence assigned to rules produced without an LLM.</summary>
    public const double DefaultConfidence = 0.5;

    public RecallRule Extract(FeedbackInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var feedback = Clean(input.Feedback);
        var cleanedTask = Clean(input.Task);
        var hasTask = cleanedTask.Length > 0;

        // With no task, use the feedback itself as the (searchable) trigger.
        var trigger = hasTask ? cleanedTask : feedback;

        var mistake = Clean(input.BadOutput) is { Length: > 0 } bad ? bad : feedback;

        return new RecallRule
        {
            Version = 1,
            Status = RuleStatus.Pending,
            Trigger = trigger,
            Mistake = mistake,
            RuleText = BuildRuleText(hasTask ? cleanedTask : null, feedback, input),
            TechnicalContext = Clean(input.ScopeValue),
            Tags = NormalizeTags(input.Tags),
            Confidence = DefaultConfidence,
            ScopeLevel = input.ScopeLevel,
            ScopeValue = Clean(input.ScopeValue),
        };
    }

    private static string BuildRuleText(string? trigger, string feedback, FeedbackInput input)
    {
        var sb = new StringBuilder();

        // Frame the rule with its trigger when we have one; otherwise the feedback
        // stands on its own.
        sb.Append(trigger is null ? feedback : $"When {trigger.TrimEnd('.')}: {feedback}");

        if (Clean(input.FixedOutput) is { Length: > 0 } fixedOutput)
        {
            sb.Append($" Prefer: {fixedOutput}");
        }

        if (Clean(input.BadOutput) is { Length: > 0 } badOutput)
        {
            sb.Append($" Avoid: {badOutput}");
        }

        return sb.ToString();
    }

    /// <summary>Trims, lowercases, de-duplicates and re-joins comma-separated tags.</summary>
    private static string NormalizeTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return string.Empty;
        }

        var normalized = tags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(tag => tag.ToLowerInvariant())
            .Distinct();

        return string.Join(",", normalized);
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
