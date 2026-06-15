using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: capture pull-request review comments as feedback. Each comment that
/// reads as a reusable correction becomes a pending rule; the rest are skipped.
/// Lets an agent feed PR review feedback (e.g. from `gh pr view`) straight into
/// AgentRecall instead of waiting for an inline correction.
/// </summary>
public sealed class ImportPrCommentsTool : IMcpTool
{
    public string Name => "import_pr_comments";

    public string Description =>
        "Capture pull-request review comments as feedback. Pass the review comments; " +
        "each one that reads as a reusable correction becomes a pending rule (tagged " +
        "pr-review), and non-actionable comments (praise, questions, nits) are " +
        "skipped. Use this to remember reviewer feedback after a PR review.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["comments"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "The review comment texts.",
                ["items"] = new JsonObject { ["type"] = "string" },
            },
            ["pr_title"] = Prop("string", "The PR title or number, used as task context."),
            ["scope_level"] = ScopeLevelProp(),
            ["scope_value"] = Prop("string", "Scope identifier (e.g. repo name)."),
            ["tags"] = Prop("string", "Extra comma-separated tags (pr-review is always added)."),
        },
        ["required"] = new JsonArray { "comments" },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var comments = ReadComments(arguments);
        if (comments.Count == 0)
        {
            throw new ArgumentException("At least one comment is required.");
        }

        var scopeLevel = ScopeLevel.Global;
        if (McpToolHelpers.TryParseScopeLevel(McpArgs.GetString(arguments, "scope_level"), out var parsed))
        {
            scopeLevel = parsed;
        }

        var options = new PullRequestImportOptions
        {
            PullRequestTitle = McpArgs.GetString(arguments, "pr_title"),
            ScopeLevel = scopeLevel,
            ScopeValue = McpArgs.GetString(arguments, "scope_value"),
            Tags = McpArgs.GetString(arguments, "tags"),
        };

        var importer = services.GetRequiredService<IPullRequestImportService>();
        var result = await importer.ImportAsync(comments, options, cancellationToken).ConfigureAwait(false);

        return new JsonObject
        {
            ["comments_found"] = result.CommentsFound,
            ["rules_created"] = result.RulesCreated,
            ["skipped"] = result.Skipped,
            ["rule_ids"] = new JsonArray([.. result.RuleIds.Select(id => (JsonNode)id)]),
            ["status"] = "Pending",
        };
    }

    private static IReadOnlyList<PullRequestComment> ReadComments(JsonObject? arguments)
    {
        if (arguments is null || !arguments.TryGetPropertyValue("comments", out var node) || node is not JsonArray array)
        {
            return [];
        }

        var comments = new List<PullRequestComment>();
        foreach (var item in array)
        {
            var body = item?.GetValue<string?>();
            if (!string.IsNullOrWhiteSpace(body))
            {
                comments.Add(new PullRequestComment { Body = body.Trim() });
            }
        }

        return comments;
    }

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private static JsonObject ScopeLevelProp()
    {
        var prop = Prop("string", "Scope granularity (default Global).");
        var values = new JsonArray();
        foreach (var name in Enum.GetNames<ScopeLevel>())
        {
            values.Add(name);
        }

        prop["enum"] = values;
        return prop;
    }
}
