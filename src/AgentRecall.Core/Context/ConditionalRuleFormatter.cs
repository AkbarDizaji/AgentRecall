using System.Text;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Context;

/// <summary>
/// Renders a rule as a conditional block — the shape AgentRecall stores knowledge
/// in and the shape it injects back:
/// <code>
/// When implementing Events backend gates:
///   Do: Use IsEventsFeatureEnabled.
///   Avoid: IsVenueMigratedFor.
///   Because: Backend and frontend gate definitions must match.
///   Source: #12
/// </code>
/// Empty parts are dropped so the block never carries fake structure. Shared by the
/// hook formatter and the CLI so both speak the same conditional language.
/// </summary>
/// <summary>How much of a rule to render.</summary>
public enum RuleDetail
{
    /// <summary>Condition, action, anti-pattern, rationale and source — the whole rule.</summary>
    Full,

    /// <summary>
    /// Condition, action and source only. What a rule offered as merely relevant needs to say:
    /// the rationale and the anti-pattern are what make a must-follow rule followable, and on a
    /// suggestion they cost two thirds of its tokens to restate guidance the action already gives.
    /// </summary>
    Compact,

    /// <summary>
    /// One line, for a rule this chat has already been shown in full. Re-sending the whole block
    /// every turn buys nothing: the agent has read it, and the reminder is there so a rule does
    /// not silently drop out of view. Periodically it is restated in full anyway, because a long
    /// chat gets compacted and what was read early may no longer be in view.
    /// </summary>
    Reminder,
}

public static class ConditionalRuleFormatter
{
    private static readonly string[] ProhibitionPrefixes =
        ["avoid ", "do not ", "don't ", "dont ", "never ", "no "];

    /// <summary>
    /// Formats a rule as a conditional block. <paramref name="indent"/> is the
    /// number of spaces before each Do/Avoid/Because/Source line; set
    /// <paramref name="includeSource"/> to append a "Source: #id" line, and
    /// <paramref name="detail"/> to <see cref="RuleDetail.Compact"/> for condition and action only.
    /// </summary>
    public static string Format(
        RecallRule rule,
        int indent = 2,
        bool includeSource = true,
        RuleDetail detail = RuleDetail.Full)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var pad = new string(' ', indent);
        var sb = new StringBuilder();

        var condition = string.IsNullOrWhiteSpace(rule.Trigger)
            ? "Always"
            : rule.Trigger.Trim().TrimEnd(':', '.', ' ');

        if (detail == RuleDetail.Reminder)
        {
            // The action, not the condition. A reminder is for a rule this chat has already read
            // in full, and its line is trimmed like any other — leading with a long condition
            // spent the whole line on it and cut away the guidance, which is the half worth
            // repeating.
            return $"#{rule.Id} still applies: {rule.RuleText.Trim()}";
        }

        sb.Append(condition).Append(':');

        if (!string.IsNullOrWhiteSpace(rule.RuleText))
        {
            sb.AppendLine().Append(pad).Append("Do: ").Append(rule.RuleText.Trim());
        }

        if (detail == RuleDetail.Full)
        {
            var avoid = StripProhibitionPrefix(rule.Mistake);
            if (!string.IsNullOrWhiteSpace(avoid))
            {
                sb.AppendLine().Append(pad).Append("Avoid: ").Append(avoid);
            }

            if (!string.IsNullOrWhiteSpace(rule.TechnicalContext))
            {
                sb.AppendLine().Append(pad).Append("Because: ").Append(rule.TechnicalContext.Trim());
            }
        }

        if (includeSource)
        {
            sb.AppendLine().Append(pad).Append("Source: #").Append(rule.Id);
            if (rule.Status == RuleStatus.Pending)
            {
                sb.Append(" (pending — not yet approved)");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Drops a leading "Avoid"/"Don't"/"Never" from the stored anti-pattern so the
    /// "Avoid:" label does not read "Avoid: Avoid …".
    /// </summary>
    private static string StripProhibitionPrefix(string? mistake)
    {
        if (string.IsNullOrWhiteSpace(mistake))
        {
            return string.Empty;
        }

        var text = mistake.Trim();
        foreach (var prefix in ProhibitionPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = text[prefix.Length..].TrimStart();
                return rest.Length > 0 ? rest : text;
            }
        }

        return text;
    }
}
