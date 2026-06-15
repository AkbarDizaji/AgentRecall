using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: capture a single piece of feedback in one step — records the event,
/// generates a Pending rule, and returns it. A lighter-weight alternative to
/// <c>add_feedback</c> when only the feedback text is available.
/// </summary>
public sealed class CaptureFeedbackTool : IMcpTool
{
    public string Name => "capture_feedback";

    public string Description =>
        "Save a correction in one step: records a feedback event and generates an " +
        "active rule from it. Keep 'feedback' to a single concise directive; put " +
        "longer rationale in 'task'. Only 'feedback' is required. Pass pending=true " +
        "to require approval before the rule takes effect.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["feedback"] = Prop("string", "The corrective guidance to remember — one concise directive."),
            ["task"] = Prop("string", "Optional: what the agent was doing when this came up."),
            ["scope_level"] = ScopeLevelProp(),
            ["scope_value"] = Prop("string", "Optional scope identifier (e.g. repo name, language, path)."),
            ["tags"] = Prop("string", "Optional comma-separated tags."),
            ["pending"] = Prop("boolean", "Optional: capture as a Pending rule that needs approval (default false)."),
        },
        ["required"] = new JsonArray { "feedback" },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var feedbackText = McpArgs.GetRequiredString(arguments, "feedback");

        var scopeLevel = ScopeLevel.Global;
        if (McpToolHelpers.TryParseScopeLevel(McpArgs.GetString(arguments, "scope_level"), out var parsed))
        {
            scopeLevel = parsed;
        }

        var input = new FeedbackInput
        {
            // Task is optional here; the extractor handles an unspecified task.
            Task = McpArgs.GetString(arguments, "task") ?? string.Empty,
            Feedback = feedbackText,
            ScopeLevel = scopeLevel,
            ScopeValue = McpArgs.GetString(arguments, "scope_value"),
            Tags = McpArgs.GetString(arguments, "tags"),
            AutoApprove = McpArgs.GetBool(arguments, "pending") is { } pending ? !pending : null,
        };

        var feedback = services.GetRequiredService<IFeedbackService>();
        var result = await feedback.AddAsync(input, cancellationToken).ConfigureAwait(false);

        return new JsonObject
        {
            ["event_id"] = result.Event.Id,
            ["rule_id"] = result.Rule.Id,
            ["status"] = result.Rule.Status.ToString(),
            ["rule"] = McpToolHelpers.ToGuidanceNode(result.Rule),
        };
    }

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private static JsonObject ScopeLevelProp()
    {
        var prop = Prop("string", "Optional scope granularity (default Global).");
        var enumValues = new JsonArray();
        foreach (var name in Enum.GetNames<ScopeLevel>())
        {
            enumValues.Add(name);
        }

        prop["enum"] = enumValues;
        return prop;
    }
}
