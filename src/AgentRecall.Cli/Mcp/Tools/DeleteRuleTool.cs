using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: permanently remove a rule from the database. Unlike <see cref="AddFeedbackTool"/>'s
/// reject path or the "archive" lifecycle transition, this is irreversible — there is no
/// status to revert once the row is gone. Prefer archiving (via <c>agentrecall rules archive</c>)
/// for routine retirement; use this only to purge a rule that should never have existed
/// (duplicates, test noise, bad captures).
/// </summary>
public sealed class DeleteRuleTool : IMcpTool
{
    public string Name => "delete_rule";

    public string Description =>
        "Permanently delete a rule by id. Irreversible — unlike archiving, there is no " +
        "status to revert. Deleting a rule that is currently in force (Active or Promoted) " +
        "requires force=true; other statuses (Draft, Pending, Archived, Superseded, Retired) " +
        "delete without it. Returns deleted=false with a reason when the rule does not exist " +
        "or force is required but missing.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["id"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "The id of the rule to delete.",
            },
            ["force"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Required to delete a rule that is currently Active or Promoted.",
            },
        },
        ["required"] = new JsonArray { "id" },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var id = McpArgs.GetInt(arguments, "id")
            ?? throw new ArgumentException("Missing required argument 'id'.");
        var force = McpArgs.GetBool(arguments, "force") ?? false;

        var lifecycle = services.GetRequiredService<IRuleLifecycleService>();
        try
        {
            var deleted = await lifecycle.DeleteAsync(id, force, cancellationToken).ConfigureAwait(false);
            return new JsonObject
            {
                ["deleted"] = true,
                ["id"] = deleted.Id,
                ["was_status"] = deleted.Status.ToString(),
            };
        }
        catch (KeyNotFoundException)
        {
            return new JsonObject
            {
                ["deleted"] = false,
                ["id"] = id,
                ["reason"] = $"Rule #{id} not found.",
            };
        }
        catch (InvalidOperationException ex)
        {
            return new JsonObject
            {
                ["deleted"] = false,
                ["id"] = id,
                ["reason"] = ex.Message,
            };
        }
    }
}
