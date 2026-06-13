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
        "Get the rules that always apply for the current project: project-scoped " +
        "rules, global rules, and promoted rules. Ordered Project → Promoted → " +
        "Active. Superseded and archived rules are excluded.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["scope_level"] = ScopeLevelProp(),
            ["scope_value"] = Prop("string", "The project/scope identifier (e.g. repo name, language, path)."),
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
            // Only rules that are actually in force.
            .Where(r => r.Status is RuleStatus.Active or RuleStatus.Promoted)
            // With a project scope, keep its rules plus global and promoted ones,
            // and drop Active rules belonging to other projects.
            .Where(r => scopeValue is null
                || r.ScopeLevel == ScopeLevel.Global
                || r.Status == RuleStatus.Promoted
                || IsProjectRule(r, scopeLevel, scopeValue))
            .OrderByDescending(r => Priority(r, scopeLevel, scopeValue))
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

    private static bool IsProjectRule(RecallRule rule, ScopeLevel? scopeLevel, string? scopeValue) =>
        !string.IsNullOrWhiteSpace(scopeValue)
        && rule.ScopeLevel != ScopeLevel.Global
        && string.Equals(rule.ScopeValue, scopeValue, StringComparison.OrdinalIgnoreCase)
        && (scopeLevel is null || rule.ScopeLevel == scopeLevel);

    /// <summary>Project rules rank above promoted, which rank above plain active.</summary>
    private static int Priority(RecallRule rule, ScopeLevel? scopeLevel, string? scopeValue)
    {
        if (IsProjectRule(rule, scopeLevel, scopeValue))
        {
            return 3;
        }

        return rule.Status switch
        {
            RuleStatus.Promoted => 2,
            RuleStatus.Active => 1,
            _ => 0,
        };
    }

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
