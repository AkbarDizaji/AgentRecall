using System.Text.Json;
using System.Text.Json.Nodes;
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
}
