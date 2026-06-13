using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: return short, high-signal reminders for a kind of work (e.g.
/// "code_review"). Only promoted or high-confidence rules are surfaced.
/// </summary>
public sealed class GetRemindersTool : IMcpTool
{
    // High-confidence cutoff mirrors the auto-promotion threshold.
    private const double HighConfidence = RuleLifecycleService.PromoteConfidenceThreshold;
    private const int DefaultLimit = 10;

    public string Name => "get_reminders";

    public string Description =>
        "Get a short checklist of reminders for a kind of task (e.g. code_review, " +
        "refactor, debug). Only promoted or high-confidence rules are returned.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["task_type"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The kind of work, e.g. code_review, refactor, debug, implement.",
            },
            ["limit"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = $"Maximum reminders to return (default {DefaultLimit}).",
            },
        },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var taskType = McpArgs.GetString(arguments, "task_type");
        var limit = McpArgs.GetInt(arguments, "limit") is { } l and > 0 ? l : DefaultLimit;

        var repository = services.GetRequiredService<IRecallRuleRepository>();
        var all = await repository.ListAsync(cancellationToken).ConfigureAwait(false);

        // Only trustworthy rules become reminders.
        var trustworthy = all
            .Where(r => r.Status == RuleStatus.Promoted || r.Confidence >= HighConfidence)
            .Where(r => r.Status is not (RuleStatus.Superseded or RuleStatus.Archived))
            .ToList();

        // If a task_type is given and any rules mention it, prefer those.
        var keywords = KeywordExtractor.Extract(taskType ?? string.Empty);
        if (keywords.Count > 0)
        {
            var matched = trustworthy.Where(r => MentionsAny(r, keywords)).ToList();
            if (matched.Count > 0)
            {
                trustworthy = matched;
            }
        }

        var reminders = new JsonArray();
        foreach (var rule in trustworthy
            .OrderByDescending(r => r.Status == RuleStatus.Promoted)
            .ThenByDescending(r => r.Confidence)
            .Take(limit))
        {
            reminders.Add(ToReminder(rule));
        }

        return new JsonObject
        {
            ["task_type"] = taskType,
            ["count"] = reminders.Count,
            ["reminders"] = reminders,
        };
    }

    private static bool MentionsAny(RecallRule rule, IReadOnlyList<string> keywords)
    {
        var haystack = $"{rule.Trigger} {rule.Tags} {rule.RuleText}".ToLowerInvariant();
        return keywords.Any(k => haystack.Contains(k, StringComparison.Ordinal));
    }

    /// <summary>Condenses a rule into a short imperative reminder.</summary>
    private static string ToReminder(RecallRule rule)
    {
        var text = rule.RuleText;

        // Drop a leading "When <trigger>: " preamble if present.
        var colon = text.IndexOf(':');
        if (text.StartsWith("When ", StringComparison.OrdinalIgnoreCase) && colon > 0 && colon < text.Length - 1)
        {
            text = text[(colon + 1)..];
        }

        // Keep the first sentence only.
        var stop = text.IndexOfAny(['.', '!', '?']);
        if (stop > 0)
        {
            text = text[..stop];
        }

        text = text.Trim();
        return text.Length > 100 ? text[..99] + "…" : text;
    }
}
