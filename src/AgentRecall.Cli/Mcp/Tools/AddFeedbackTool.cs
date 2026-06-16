using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>MCP tool: record feedback and extract a pending rule from it.</summary>
public sealed class AddFeedbackTool : IMcpTool
{
    public string Name => "add_feedback";

    public string Description =>
        "Record corrective feedback about an agent's work. Stores the raw feedback " +
        "and extracts a technical rule for future recall (active by default; pass " +
        "pending=true to require approval first).";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["task"] = Prop("string", "What the agent was asked to do."),
            ["feedback"] = Prop("string", "The corrective guidance."),
            ["bad_output"] = Prop("string", "The undesirable output that was produced."),
            ["fixed_output"] = Prop("string", "The corrected or preferred output."),
            ["scope_level"] = ScopeLevelProp(),
            ["scope_value"] = Prop("string", "Scope identifier (e.g. repo name, language, path)."),
            ["tags"] = Prop("string", "Comma-separated tags."),
            ["pending"] = Prop("boolean", "Capture as a Pending rule that needs approval (default false)."),
        },
        ["required"] = new JsonArray { "task", "feedback" },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var scopeLevel = ScopeLevel.Global;
        if (McpToolHelpers.TryParseScopeLevel(McpArgs.GetString(arguments, "scope_level"), out var parsed))
        {
            scopeLevel = parsed;
        }

        var input = new FeedbackInput
        {
            Task = McpArgs.GetRequiredString(arguments, "task"),
            Feedback = McpArgs.GetRequiredString(arguments, "feedback"),
            BadOutput = McpArgs.GetString(arguments, "bad_output"),
            FixedOutput = McpArgs.GetString(arguments, "fixed_output"),
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
            ["reused_existing_rule"] = result.ReusedExistingRule,
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
        var prop = Prop("string", "Scope granularity (default Global).");
        var enumValues = new JsonArray();
        foreach (var name in Enum.GetNames<ScopeLevel>())
        {
            enumValues.Add(name);
        }

        prop["enum"] = enumValues;
        return prop;
    }
}
