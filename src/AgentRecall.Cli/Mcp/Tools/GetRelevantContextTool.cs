using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Search;
using AgentRecall.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: given a task description, surface the rules an agent should know
/// before starting. Extracts keywords and runs a ranked search; returns an empty
/// list when nothing is relevant.
/// </summary>
public sealed class GetRelevantContextTool : IMcpTool
{
    private const int DefaultLimit = 10;

    public string Name => "get_relevant_context";

    public string Description =>
        "Before coding, reviewing, refactoring or debugging, fetch the rules " +
        "relevant to a task. Extracts keywords, searches stored rules, and returns " +
        "the most relevant active guidance (empty if none applies).";

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
            ["limit"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = $"Maximum rules to return (default {DefaultLimit}).",
            },
        },
        ["required"] = new JsonArray { "task" },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var task = McpArgs.GetRequiredString(arguments, "task");
        var limit = McpArgs.GetInt(arguments, "limit") is { } l and > 0 ? l : DefaultLimit;

        var keywords = KeywordExtractor.Extract(task);
        var rules = new JsonArray();

        if (keywords.Count > 0)
        {
            var search = services.GetRequiredService<IRecallSearchService>();
            var results = await search
                .SearchAsync(string.Join(' ', keywords), new SearchOptions { Limit = limit }, cancellationToken)
                .ConfigureAwait(false);

            foreach (var result in results)
            {
                rules.Add(McpToolHelpers.ToGuidanceNode(result.Rule));
            }
        }

        return new JsonObject
        {
            ["task"] = task,
            ["keywords"] = new JsonArray([.. keywords.Select(k => (JsonNode)k)]),
            ["count"] = rules.Count,
            ["rules"] = rules,
        };
    }
}
