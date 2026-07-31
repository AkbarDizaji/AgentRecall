using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: act on the user's reply to an "awaiting your approval" prompt (surfaced in the
/// Turn Memory Summary whenever a capture is parked pending approval — the default for every
/// automatic Stop-hook capture). Call this once the user replies yes/no for a specific rule, or
/// "yes to all"/"no to all" for every rule still pending in the chat.
/// </summary>
public sealed class ResolvePendingCaptureTool : IMcpTool
{
    public string Name => "resolve_pending_capture";

    public string Description =>
        "Resolve a rule AgentRecall parked pending approval, per the user's chat reply. " +
        "decision=approve/reject act on one rule_id; approve_all/reject_all act on every rule " +
        "still awaiting approval in the chat (session_id optional — defaults to the most " +
        "recently captured chat with something outstanding). Call this after the user replies " +
        "yes/no to an \"Awaiting your approval\" prompt from the Turn Memory Summary.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["decision"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "approve", "reject", "approve_all", "reject_all" },
                ["description"] = "approve/reject act on rule_id; approve_all/reject_all act on every rule pending in the chat.",
            },
            ["rule_id"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Required for decision=approve or decision=reject.",
            },
            ["session_id"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional for approve_all/reject_all; omit to default to the most recent chat with rules pending.",
            },
        },
        ["required"] = new JsonArray { "decision" },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var decision = McpArgs.GetString(arguments, "decision") ?? string.Empty;
        var approvals = services.GetRequiredService<IPendingCaptureApprovalService>();

        switch (decision)
        {
            case "approve":
            case "reject":
                return await ResolveOneAsync(arguments, approvals, approve: decision == "approve", cancellationToken)
                    .ConfigureAwait(false);

            case "approve_all":
            case "reject_all":
                return await ResolveAllAsync(arguments, approvals, approve: decision == "approve_all", cancellationToken)
                    .ConfigureAwait(false);

            default:
                return new JsonObject
                {
                    ["resolved"] = false,
                    ["reason"] = $"Unknown decision '{decision}'. Expected approve, reject, approve_all, or reject_all.",
                };
        }
    }

    private static async Task<JsonNode> ResolveOneAsync(
        JsonObject? arguments, IPendingCaptureApprovalService approvals, bool approve, CancellationToken cancellationToken)
    {
        var ruleId = McpArgs.GetInt(arguments, "rule_id");
        if (ruleId is not { } id)
        {
            return new JsonObject { ["resolved"] = false, ["reason"] = "Missing required argument 'rule_id'." };
        }

        try
        {
            var rule = approve
                ? await approvals.ApproveAsync(id, cancellationToken).ConfigureAwait(false)
                : await approvals.RejectAsync(id, cancellationToken).ConfigureAwait(false);
            return new JsonObject { ["resolved"] = true, ["rule_id"] = rule.Id, ["status"] = rule.Status.ToString() };
        }
        catch (KeyNotFoundException ex)
        {
            return new JsonObject { ["resolved"] = false, ["rule_id"] = id, ["reason"] = ex.Message };
        }
        catch (InvalidOperationException ex)
        {
            return new JsonObject { ["resolved"] = false, ["rule_id"] = id, ["reason"] = ex.Message };
        }
    }

    private static async Task<JsonNode> ResolveAllAsync(
        JsonObject? arguments, IPendingCaptureApprovalService approvals, bool approve, CancellationToken cancellationToken)
    {
        var sessionId = McpArgs.GetString(arguments, "session_id");
        var batch = approve
            ? await approvals.ApproveAllAsync(sessionId, cancellationToken).ConfigureAwait(false)
            : await approvals.RejectAllAsync(sessionId, cancellationToken).ConfigureAwait(false);

        var ids = new JsonArray();
        foreach (var id in batch.RuleIds)
        {
            ids.Add(id);
        }

        return new JsonObject
        {
            ["resolved"] = batch.RuleIds.Count > 0,
            ["rule_ids"] = ids,
            ["count"] = batch.RuleIds.Count,
            ["session_id"] = batch.SessionId,
            ["reason"] = batch.RuleIds.Count == 0 ? "Nothing is awaiting approval." : null,
        };
    }
}
