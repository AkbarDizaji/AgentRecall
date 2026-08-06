using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: a client reports that a rule it just retrieved is wrong, corrupted, or
/// otherwise unusable. Archives the rule immediately rather than waiting on the
/// gradual confidence decay of the outcome-tracking pipeline, since a rule that is
/// actively broken should not keep being injected while it decays.
/// </summary>
public sealed class ReportBadRuleTool : IMcpTool
{
    public string Name => "report_bad_rule";

    public string Description =>
        "Report a rule as wrong, corrupted, or unusable because you just encountered it " +
        "in practice. Archives the rule immediately so it stops being injected. Use this " +
        "instead of add_feedback's outcome tracking when the rule itself is broken, not " +
        "merely low-confidence.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["id"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "The id of the rule to report.",
            },
            ["reason"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Why the rule is wrong, corrupted, or unusable.",
            },
        },
        ["required"] = new JsonArray { "id" },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var id = McpArgs.GetInt(arguments, "id")
            ?? throw new ArgumentException("Missing required argument 'id'.");
        var reason = McpArgs.GetString(arguments, "reason");

        var lifecycle = services.GetRequiredService<IRuleLifecycleService>();
        try
        {
            var archived = await lifecycle.ReportBadAsync(id, reason, cancellationToken).ConfigureAwait(false);
            return new JsonObject
            {
                ["archived"] = true,
                ["id"] = archived.Id,
                ["status"] = archived.Status.ToString(),
            };
        }
        catch (KeyNotFoundException)
        {
            return new JsonObject
            {
                ["archived"] = false,
                ["id"] = id,
                ["reason"] = $"Rule #{id} not found.",
            };
        }
    }
}
