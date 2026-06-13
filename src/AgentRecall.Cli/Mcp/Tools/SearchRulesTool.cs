using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Search;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>MCP tool: search stored rules and return ranked coding guidance.</summary>
public sealed class SearchRulesTool : IMcpTool
{
    public string Name => "search_rules";

    public string Description =>
        "Search AgentRecall for technical coding rules relevant to a query. " +
        "Returns ranked guidance (trigger, rule, do, do_not, reason, applies_to, " +
        "confidence, status). Superseded and archived rules are excluded.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = Prop("string", "What the agent is about to do, or a topic to recall rules for."),
            ["scope_level"] = ScopeLevelProp(),
            ["scope_value"] = Prop("string", "Scope identifier to filter by (e.g. repo name, language, path)."),
            ["file_path"] = Prop("string", "Current file path; used as a File-level scope filter when no scope is given."),
            ["limit"] = Prop("integer", "Maximum number of results (default 20)."),
        },
        ["required"] = new JsonArray { "query" },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var query = McpArgs.GetRequiredString(arguments, "query");

        var options = new SearchOptions();

        if (McpToolHelpers.TryParseScopeLevel(McpArgs.GetString(arguments, "scope_level"), out var level))
        {
            options = options with { ScopeLevel = level };
        }

        var scopeValue = McpArgs.GetString(arguments, "scope_value");
        if (scopeValue is not null)
        {
            options = options with { ScopeValue = scopeValue };
        }

        // file_path acts as a File-scope filter only when no explicit scope was given.
        var filePath = McpArgs.GetString(arguments, "file_path");
        if (filePath is not null && options.ScopeLevel is null && options.ScopeValue is null)
        {
            options = options with { ScopeLevel = ScopeLevel.File, ScopeValue = filePath };
        }

        if (McpArgs.GetInt(arguments, "limit") is { } limit && limit > 0)
        {
            options = options with { Limit = limit };
        }

        var search = services.GetRequiredService<IRecallSearchService>();
        var results = await search.SearchAsync(query, options, cancellationToken).ConfigureAwait(false);

        var rules = new JsonArray();
        foreach (var result in results)
        {
            rules.Add(McpToolHelpers.ToGuidanceNode(result.Rule));
        }

        return new JsonObject
        {
            ["query"] = query,
            ["count"] = results.Count,
            ["rules"] = rules,
        };
    }

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private static JsonObject ScopeLevelProp()
    {
        var prop = Prop("string", "Scope granularity to filter by.");
        var enumValues = new JsonArray();
        foreach (var name in Enum.GetNames<ScopeLevel>())
        {
            enumValues.Add(name);
        }

        prop["enum"] = enumValues;
        return prop;
    }
}
