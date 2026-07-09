using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: fetch a single rule by its id. Unlike <see cref="SearchRulesTool"/>
/// (a relevance search that answers "what is relevant to this topic"), this is an
/// exact lookup that answers "what is rule N" — one id in, that one rule out. It is
/// the deterministic way to verify a just-captured rule's stored content instead of
/// inferring it from a ranked search.
///
/// It returns the rule regardless of status (including Superseded/Archived), because
/// the point is to show exactly what lives under the id; the status field names the
/// rule's lifecycle state so a retired rule is never mistaken for an in-force one.
/// </summary>
public sealed class GetRuleTool : IMcpTool
{
    public string Name => "get_rule";

    public string Description =>
        "Fetch a single stored rule by its id and return its exact guidance " +
        "(trigger, rule, do, do_not, reason, applies_to, confidence, status). " +
        "Use this to verify a rule's stored content by id; unlike search_rules it " +
        "does not rank or match on a topic. Returns found=false when no rule has the id.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["id"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "The id of the rule to fetch.",
            },
        },
        ["required"] = new JsonArray { "id" },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var id = McpArgs.GetInt(arguments, "id")
            ?? throw new ArgumentException("Missing required argument 'id'.");

        var rules = services.GetRequiredService<IRecallRuleRepository>();
        var rule = await rules.GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (rule is null)
        {
            return new JsonObject
            {
                ["found"] = false,
                ["id"] = id,
            };
        }

        return new JsonObject
        {
            ["found"] = true,
            ["rule"] = McpToolHelpers.ToGuidanceNode(rule),
        };
    }
}
