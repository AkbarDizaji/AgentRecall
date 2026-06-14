using System.Text.Json.Nodes;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Policy;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: given a task, resolve every matching rule into the ones that are
/// effective and the ones that should be ignored, settling conflicts (e.g. "use
/// the repository pattern" vs "do not use the repository pattern") automatically.
/// </summary>
public sealed class ResolveRulesTool : IMcpTool
{
    public string Name => "resolve_rules";

    public string Description =>
        "When several rules might apply to a task, resolve them into the rules to " +
        "follow and the rules to ignore. Detects direct conflicts and superseded " +
        "rules, prefers project-specific over global, and explains each decision. " +
        "Precedence: project scope → explicit supersede → priority → newer → " +
        "higher confidence.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["task"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "What you are about to do, in plain language.",
            },
            ["scope_level"] = ScopeLevelProp(),
            ["scope_value"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The project/scope identifier (e.g. repo name) to prefer rules for.",
            },
        },
        ["required"] = new JsonArray { "task" },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var task = McpArgs.GetRequiredString(arguments, "task");
        var context = new PolicyContext
        {
            ScopeLevel = McpToolHelpers.TryParseScopeLevel(McpArgs.GetString(arguments, "scope_level"), out var level)
                ? level
                : null,
            ScopeValue = McpArgs.GetString(arguments, "scope_value"),
        };

        var engine = services.GetRequiredService<IPolicyEngine>();
        var resolution = await engine.ResolveForTaskAsync(task, context, cancellationToken).ConfigureAwait(false);

        return new JsonObject
        {
            ["task"] = task,
            ["effective_count"] = resolution.Effective.Count,
            ["ignored_count"] = resolution.Ignored.Count,
            ["effective"] = ToVerdictArray(resolution.Effective),
            ["ignored"] = ToVerdictArray(resolution.Ignored),
            ["conflicts"] = ToConflictArray(resolution.Conflicts),
            ["explanation"] = resolution.Explanation,
        };
    }

    private static JsonArray ToVerdictArray(IReadOnlyList<RuleVerdict> verdicts)
    {
        var array = new JsonArray();
        foreach (var verdict in verdicts)
        {
            var node = McpToolHelpers.ToGuidanceNode(verdict.Rule).AsObject();
            node["reason"] = verdict.Reason;
            array.Add(node);
        }

        return array;
    }

    private static JsonArray ToConflictArray(IReadOnlyList<RuleConflict> conflicts)
    {
        var array = new JsonArray();
        foreach (var conflict in conflicts)
        {
            array.Add(new JsonObject
            {
                ["subject"] = conflict.Subject,
                ["winner_id"] = conflict.Winner.Id,
                ["ignored_ids"] = new JsonArray([.. conflict.Losers.Select(l => (JsonNode)l.Id)]),
                ["reason"] = conflict.Reason,
            });
        }

        return array;
    }

    private static JsonObject ScopeLevelProp()
    {
        var prop = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Scope granularity of the task, to prefer matching project rules.",
        };

        var enumValues = new JsonArray();
        foreach (var name in Enum.GetNames<ScopeLevel>())
        {
            enumValues.Add(name);
        }

        prop["enum"] = enumValues;
        return prop;
    }
}
