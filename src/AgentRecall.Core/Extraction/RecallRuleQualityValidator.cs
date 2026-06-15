using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Extraction;

/// <summary>A single quality problem found on a rule.</summary>
public sealed record RuleQualityIssue(string Field, string Message);

/// <summary>The outcome of validating a rule: the (sanitised) rule plus issues.</summary>
public sealed record RuleQualityResult(bool IsValid, RecallRule Rule, IReadOnlyList<RuleQualityIssue> Issues);

/// <summary>Validates and sanitises an extracted rule's structured fields.</summary>
public interface IRecallRuleQualityValidator
{
    RuleQualityResult Validate(RecallRule rule);
}

/// <summary>
/// Default <see cref="IRecallRuleQualityValidator"/>. Rather than reject a rule, it
/// blanks fields that fail their check — so the stored rule never carries fake
/// structure — and lowers confidence when a core field (rule text or trigger) is
/// missing. Checks:
/// <list type="bullet">
///   <item>rule text is not empty;</item>
///   <item>trigger is not empty;</item>
///   <item>reason is not the scope value;</item>
///   <item>do and do_not are not equivalent;</item>
///   <item>do_not, if present, is actually prohibitive.</item>
/// </list>
/// </summary>
public sealed class RecallRuleQualityValidator : IRecallRuleQualityValidator
{
    /// <summary>Confidence ceiling applied to a rule missing a core field.</summary>
    public const double InvalidConfidenceCeiling = 0.2;

    public RuleQualityResult Validate(RecallRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var issues = new List<RuleQualityIssue>();

        // Reason must never be the scope value (a past bug fed scope into reason).
        if (!string.IsNullOrWhiteSpace(rule.TechnicalContext)
            && !string.IsNullOrWhiteSpace(rule.ScopeValue)
            && string.Equals(rule.TechnicalContext.Trim(), rule.ScopeValue.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            rule.TechnicalContext = string.Empty;
            issues.Add(new RuleQualityIssue("reason", "Reason must not be the scope value."));
        }

        // A present do_not must read as a prohibition.
        if (!string.IsNullOrWhiteSpace(rule.Mistake) && !ExtractionHeuristics.IsNegative(rule.Mistake))
        {
            rule.Mistake = string.Empty;
            issues.Add(new RuleQualityIssue("do_not", "do_not was not prohibitive."));
        }

        // do and do_not must not say the same thing.
        if (!string.IsNullOrWhiteSpace(rule.Mistake) && ExtractionHeuristics.Equivalent(rule.RuleText, rule.Mistake))
        {
            rule.Mistake = string.Empty;
            issues.Add(new RuleQualityIssue("do_not", "do and do_not are equivalent."));
        }

        if (string.IsNullOrWhiteSpace(rule.Trigger))
        {
            issues.Add(new RuleQualityIssue("trigger", "Trigger is empty."));
        }

        if (string.IsNullOrWhiteSpace(rule.RuleText))
        {
            issues.Add(new RuleQualityIssue("rule", "Rule text is empty."));
        }

        var isValid = !issues.Any(i => i.Field is "rule" or "trigger");
        if (!isValid)
        {
            // Don't fake structure — keep it, but flag it as low-confidence.
            rule.Confidence = Math.Min(rule.Confidence, InvalidConfidenceCeiling);
        }

        return new RuleQualityResult(isValid, rule, issues);
    }
}
