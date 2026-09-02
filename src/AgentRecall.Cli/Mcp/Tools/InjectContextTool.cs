using System.Text.Json.Nodes;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: assemble the most useful rules for a task — ranked by relevance and
/// confidence, conflict-resolved, and packed into a token budget — bucketed into
/// must-follow, suggested and warnings, each with an explanation. Surfaces rules
/// by meaning, not just keywords (e.g. a Money rule for "add refund support").
/// </summary>
public sealed class InjectContextTool : IMcpTool
{
    public string Name => "inject_context";

    public string Description =>
        "Before starting a task, get the most relevant rules ranked by usefulness " +
        "(not just keyword matches). Returns must-follow rules, suggested rules and " +
        "warnings, each with an explanation, within a token budget. Provide the " +
        "task, optionally its type, project scope, file names and changed entities.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["task"] = Prop("string", "What you are about to do, in plain language."),
            ["task_type"] = TaskTypeProp(),
            ["scope_level"] = ScopeLevelProp(),
            ["scope_value"] = Prop("string", "Project/scope identifier (e.g. repo name)."),
            ["file_names"] = StringArrayProp("Files the task touches."),
            ["file_path"] = Prop("string", "A single file the task touches (merged with file_names)."),
            ["changed_entities"] = StringArrayProp("Code entities being added or changed (types, methods, concepts)."),
            ["token_budget"] = Prop("integer", "Approximate token budget for the context (default 1500)."),
            ["limit"] = Prop("integer", "Maximum number of rules to return (default 25)."),
            ["include_pending"] = Prop("boolean", "Also include Pending rules, never as must-follow (default false)."),
        },
        ["required"] = new JsonArray { "task" },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var request = new ContextRequest
        {
            Task = McpArgs.GetRequiredString(arguments, "task"),
            TaskType = Enum.TryParse<TaskType>(McpArgs.GetString(arguments, "task_type"), ignoreCase: true, out var type)
                ? type
                : TaskType.Unknown,
            ScopeLevel = McpToolHelpers.TryParseScopeLevel(McpArgs.GetString(arguments, "scope_level"), out var level)
                ? level
                : null,
            ScopeValue = McpArgs.GetString(arguments, "scope_value"),
            FileNames = MergeFiles(ReadStringArray(arguments, "file_names"), McpArgs.GetString(arguments, "file_path")),
            ChangedEntities = ReadStringArray(arguments, "changed_entities"),
            TokenBudget = McpArgs.GetInt(arguments, "token_budget") is { } b and > 0 ? b : 1500,
            Limit = McpArgs.GetInt(arguments, "limit") is { } l and > 0 ? l : 25,
            IncludePending = McpArgs.GetBool(arguments, "include_pending") ?? false,
        };

        var service = services.GetRequiredService<IContextInjectionService>();
        var result = await service.BuildContextAsync(request, cancellationToken).ConfigureAwait(false);

        return new JsonObject
        {
            ["must_follow"] = ToArray(result.MustFollow),
            ["warnings"] = ToArray(result.Warnings),
            ["suggested"] = ToArray(result.Suggested),
            ["preferred_patterns"] = ToStringArray(ContextProjection.PreferredPatterns(result)),
            ["anti_patterns"] = ToStringArray(ContextProjection.AntiPatterns(result)),
            ["source_rule_ids"] = new JsonArray([.. ContextProjection.SourceRuleIds(result).Select(id => (JsonNode)id)]),
            // The handle an outcome attaches to: the caller is the only party that can report
            // how these rules actually fared, so it needs the id they were recorded under.
            ["retrieval_id"] = result.RetrievalId,
            ["tokens_used"] = result.TokensUsed,
            ["token_budget"] = result.TokenBudget,
            ["explanation"] = result.Explanation,
        };
    }

    private static IReadOnlyList<string> MergeFiles(IReadOnlyList<string> files, string? single)
    {
        if (string.IsNullOrWhiteSpace(single))
        {
            return files;
        }

        return files.Append(single).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static JsonArray ToStringArray(IReadOnlyList<string> values) =>
        new([.. values.Select(v => (JsonNode)v)]);

    private static JsonArray ToArray(IReadOnlyList<InjectedRule> rules)
    {
        var array = new JsonArray();
        foreach (var injected in rules)
        {
            var node = McpToolHelpers.ToGuidanceNode(injected.Rule).AsObject();
            node["importance"] = injected.Importance.ToString();
            node["score"] = injected.Score;
            node["relevance"] = injected.Relevance;
            node["explanation"] = injected.Explanation;
            node["estimated_tokens"] = injected.EstimatedTokens;
            node["match_reasons"] = new JsonArray([.. injected.MatchReasons.Select(r => (JsonNode)r)]);
            array.Add(node);
        }

        return array;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonObject? arguments, string key)
    {
        if (arguments is null || !arguments.TryGetPropertyValue(key, out var node) || node is not JsonArray array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in array)
        {
            var value = item?.GetValue<string?>();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private static JsonObject StringArrayProp(string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new JsonObject { ["type"] = "string" },
    };

    private static JsonObject TaskTypeProp()
    {
        var prop = Prop("string", "The kind of work (e.g. Feature, BugFix, Security).");
        var values = new JsonArray();
        foreach (var name in Enum.GetNames<TaskType>())
        {
            values.Add(name);
        }

        prop["enum"] = values;
        return prop;
    }

    private static JsonObject ScopeLevelProp()
    {
        var prop = Prop("string", "Scope granularity of the work.");
        var values = new JsonArray();
        foreach (var name in Enum.GetNames<ScopeLevel>())
        {
            values.Add(name);
        }

        prop["enum"] = values;
        return prop;
    }
}
