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
public static class ConditionalRuleFormatter
{
    private static readonly string[] ProhibitionPrefixes =
        ["avoid ", "do not ", "don't ", "dont ", "never ", "no "];

    /// <summary>
    /// Formats a rule as a conditional block. <paramref name="indent"/> is the
    /// number of spaces before each Do/Avoid/Because/Source line; set
    /// <paramref name="includeSource"/> to append a "Source: #id" line.
    /// </summary>
    public static string Format(RecallRule rule, int indent = 2, bool includeSource = true)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var pad = new string(' ', indent);
        var sb = new StringBuilder();

        var condition = string.IsNullOrWhiteSpace(rule.Trigger)
            ? "Always"
            : rule.Trigger.Trim().TrimEnd(':', '.', ' ');
        sb.Append(condition).Append(':');

        if (!string.IsNullOrWhiteSpace(rule.RuleText))
        {
            sb.AppendLine().Append(pad).Append("Do: ").Append(rule.RuleText.Trim());
        }

        var avoid = StripProhibitionPrefix(rule.Mistake);
        if (!string.IsNullOrWhiteSpace(avoid))
        {
            sb.AppendLine().Append(pad).Append("Avoid: ").Append(avoid);
        }

        if (!string.IsNullOrWhiteSpace(rule.TechnicalContext))
        {
            sb.AppendLine().Append(pad).Append("Because: ").Append(rule.TechnicalContext.Trim());
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
