using System.Globalization;
using System.Text.Json.Nodes;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: report the last turn finalization so the agent can answer "did
/// AgentRecall capture anything?" from the recorded decision instead of guessing.
/// Returns the captured/suggested/skipped/duplicate rule ids, the timestamp and
/// source, and a one-line summary in the exact phrasing the agent should echo.
/// </summary>
public sealed class CaptureStatusTool : IMcpTool
{
    public string Name => "capture_status";

    public string Description =>
        "Report the last AgentRecall turn-finalization result (captured, suggested, " +
        "and skipped rules). Call this before answering whether AgentRecall captured, " +
        "saved, or remembered anything — never guess and never say the Stop hook 'may " +
        "have' captured it. Equivalent to `agentrecall finalize-turn status`.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["cwd"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional working directory to scope the lookup to.",
            },
        },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var cwd = McpArgs.GetString(arguments, "cwd");
        var finalizer = services.GetRequiredService<ITurnFinalizer>();
        var result = await finalizer.GetLastAsync(cwd, cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            return new JsonObject
            {
                ["found"] = false,
                ["summary"] = TurnFinalizationFormatter.NoFinalization,
            };
        }

        return new JsonObject
        {
            ["found"] = true,
            ["summary"] = TurnFinalizationFormatter.SummaryLine(result),
            ["captured"] = Lessons(result.Captured),
            ["suggested"] = Lessons(result.Suggested),
            ["skipped"] = Skips(result.Skipped),
            ["captured_rule_ids"] = Ids(result.Captured.Select(l => l.RuleId)),
            ["suggested_rule_ids"] = Ids(result.Suggested.Select(l => l.RuleId)),
            ["duplicate_rule_ids"] = Ids(result.Duplicates),
            ["skipped_reasons"] = Strings(result.Skipped.Select(s => s.Reason)),
            ["errors"] = Strings(result.Errors),
            ["created_at"] = result.CreatedAt?.ToString("O", CultureInfo.InvariantCulture),
            ["source"] = result.Source,
        };
    }

    private static JsonArray Lessons(IReadOnlyList<FinalizedLesson> lessons)
    {
        var array = new JsonArray();
        foreach (var lesson in lessons)
        {
            array.Add(new JsonObject
            {
                ["rule_id"] = lesson.RuleId,
                ["category"] = lesson.Category.ToString(),
                ["scope"] = lesson.ScopeLabel,
                ["confidence"] = lesson.Confidence,
                ["text"] = lesson.Text,
                ["note"] = lesson.Note,
            });
        }

        return array;
    }

    private static JsonArray Skips(IReadOnlyList<SkippedLesson> skips)
    {
        var array = new JsonArray();
        foreach (var skip in skips)
        {
            array.Add(new JsonObject
            {
                ["reason"] = skip.Reason,
                ["duplicate_of_rule_id"] = skip.DuplicateOfRuleId,
            });
        }

        return array;
    }

    private static JsonArray Ids(IEnumerable<int> ids)
    {
        var array = new JsonArray();
        foreach (var id in ids)
        {
            array.Add(id);
        }

        return array;
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
