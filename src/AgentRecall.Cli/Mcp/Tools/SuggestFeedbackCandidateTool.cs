using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: inspect a conversation message and report whether it looks like a
/// reusable correction worth saving, along with a suggested rule. It never saves
/// anything — use <c>capture_feedback</c> to persist.
/// </summary>
public sealed class SuggestFeedbackCandidateTool : IMcpTool
{
    public string Name => "suggest_feedback_candidate";

    public string Description =>
        "Check whether a user message is a reusable coding correction (e.g. " +
        "\"don't use string interpolation for SQL\"). Returns is_candidate and a " +
        "suggested_rule. Does not save anything.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["conversation_message"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "A message from the user to inspect for reusable feedback.",
            },
        },
        ["required"] = new JsonArray { "conversation_message" },
    };

    public Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var message = McpArgs.GetRequiredString(arguments, "conversation_message");

        var analyzer = services.GetRequiredService<IFeedbackCandidateAnalyzer>();
        var candidate = analyzer.Analyze(message);

        JsonNode result = new JsonObject
        {
            ["is_candidate"] = candidate.IsCandidate,
            ["suggested_rule"] = candidate.IsCandidate ? candidate.SuggestedRule : null,
        };

        return Task.FromResult(result);
    }
}
