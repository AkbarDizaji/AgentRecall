using System.Text.Json.Nodes;
using AgentRecall.Core.Concepts;
using AgentRecall.Core.Context;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: pin an existing rule to the paths and symbols it belongs to, or unpin it. An
/// anchored rule is delivered when the work reaches that place — even when the agent only reads a
/// file there — and held back from work that is plainly somewhere else.
/// </summary>
public sealed class AnchorRuleTool : IMcpTool
{
    public string Name => "anchor_rule";

    public string Description =>
        "Pin an existing rule (or concept) to where it belongs in the code: path patterns relative to " +
        "the repository (\"tests/**\", \"src/Billing\") and class or other symbol names (\"OrderService\"). " +
        "Use it when the user says a rule is for a specific folder, file, or class. Set remove=true to " +
        "unpin the given anchors instead. Returns the rule's anchors afterwards.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["rule_id"] = new JsonObject { ["type"] = "integer", ["description"] = "The rule to place." },
            ["paths"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Path patterns relative to the repository: ** spans directories, * stays within a name.",
            },
            ["symbols"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Class or other symbol names.",
            },
            ["remove"] = new JsonObject { ["type"] = "boolean", ["description"] = "True to remove these anchors instead of adding them." },
        },
        ["required"] = new JsonArray { "rule_id" },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var id = McpArgs.GetInt(arguments, "rule_id")
            ?? throw new ArgumentException("Missing required argument 'rule_id'.");
        var anchors = McpAnchors.Read(arguments, out var rejected);
        var remove = McpArgs.GetBool(arguments, "remove") ?? false;

        var placement = services.GetRequiredService<KnowledgePlacement>();
        var rule = remove
            ? await placement.PlaceRuleAsync(id, [], anchors, cancellationToken).ConfigureAwait(false)
            : await placement.PlaceRuleAsync(id, anchors, [], cancellationToken).ConfigureAwait(false);

        if (rule is null)
        {
            return new JsonObject { ["placed"] = false, ["rule_id"] = id, ["reason"] = $"Rule #{id} not found." };
        }

        return new JsonObject
        {
            ["placed"] = true,
            ["rule_id"] = id,
            ["anchors"] = new JsonArray([.. RuleAnchors.Parse(rule.Anchors).Select(a => (JsonNode)a.ToString())]),
            ["rejected_anchors"] = new JsonArray([.. rejected.Select(r => (JsonNode)r)]),
        };
    }
}
