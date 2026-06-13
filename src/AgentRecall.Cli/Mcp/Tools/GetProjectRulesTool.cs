using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: return the applicable rules for a project/scope (no query), so an
/// agent can load relevant guidance up front.
/// </summary>
public sealed class GetProjectRulesTool : IMcpTool
{
    public string Name => "get_project_rules";

    public string Description =>
        "Get all applicable coding rules for the current project or scope, ranked " +
        "by status and confidence. Superseded and archived rules are excluded.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["scope_level"] = ScopeLevelProp(),
            ["scope_value"] = Prop("string", "Scope identifier to filter by (e.g. repo name, language, path)."),
        },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        ScopeLevel? scopeLevel = McpToolHelpers.TryParseScopeLevel(McpArgs.GetString(arguments, "scope_level"), out var level)
            ? level
            : null;
        var scopeValue = McpArgs.GetString(arguments, "scope_value");

        var repository = services.GetRequiredService<IRecallRuleRepository>();
        var all = await repository.ListAsync(cancellationToken).ConfigureAwait(false);

        var applicable = all
            .Where(r => !McpToolHelpers.ExcludedStatuses.Contains(r.Status))
            .Where(r => scopeLevel is null || r.ScopeLevel == scopeLevel)
            .Where(r => scopeValue is null || string.Equals(r.ScopeValue, scopeValue, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => StatusRank(r.Status))
            .ThenByDescending(r => r.Confidence)
            .ToList();

        var rules = new JsonArray();
        foreach (var rule in applicable)
        {
            rules.Add(McpToolHelpers.ToGuidanceNode(rule));
        }

        return new JsonObject
        {
            ["count"] = applicable.Count,
            ["rules"] = rules,
        };
    }

    private static int StatusRank(RuleStatus status) => status switch
    {
        RuleStatus.Promoted => 5,
        RuleStatus.Active => 4,
        RuleStatus.Pending => 3,
        RuleStatus.Draft => 2,
        RuleStatus.Retired => 1,
        _ => 0,
    };

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private static JsonObject ScopeLevelProp()
    {
        var prop = Prop("string", "Scope granularity to filter by.");
        var enumValues = new JsonArray();
        foreach (var name in Enum.GetNames<ScopeLevel>())
        {
            enumValues.Add(name);
        }

        prop["enum"] = enumValues;
        return prop;
    }
}
