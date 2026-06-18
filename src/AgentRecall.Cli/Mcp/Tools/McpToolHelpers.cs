using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>Shared helpers used by the MCP tools.</summary>
internal static class McpToolHelpers
{
    /// <summary>Statuses excluded from any agent-facing output.</summary>
    public static readonly HashSet<RuleStatus> ExcludedStatuses = [RuleStatus.Superseded, RuleStatus.Archived];

    public static bool TryParseScopeLevel(string? value, out ScopeLevel level)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return Enum.TryParse(value, ignoreCase: true, out level);
        }

        level = default;
        return false;
    }

    /// <summary>Serializes a rule into its agent-facing guidance JSON node.</summary>
    public static JsonNode ToGuidanceNode(RecallRule rule) =>
        JsonSerializer.SerializeToNode(RuleGuidance.From(rule), McpJson.Options)!;

    /// <summary>
    /// Serializes a feedback-capture outcome. When the memory-worthiness policy
    /// rejected the candidate as a low-value code fact, <c>stored</c> is false, no
    /// rule is returned, and the verdict explains why.
    /// </summary>
    public static JsonNode ToFeedbackResultNode(FeedbackResult result)
    {
        var node = new JsonObject
        {
            ["stored"] = result.RuleStored,
            ["reused_existing_rule"] = result.ReusedExistingRule,
        };

        if (result.Worthiness is { } worthiness)
        {
            node["worthiness"] = worthiness.Verdict.ToString();
            node["worthiness_reason"] = worthiness.Reason;
        }

        if (result.Rule is { } rule)
        {
            node["rule_id"] = rule.Id;
            node["status"] = rule.Status.ToString();
            node["rule"] = ToGuidanceNode(rule);
        }

        if (result.Event is { } recallEvent)
        {
            node["event_id"] = recallEvent.Id;
        }

        return node;
    }
}
