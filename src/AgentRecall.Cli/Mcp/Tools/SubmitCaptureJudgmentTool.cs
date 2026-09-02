using System.Text.Json.Nodes;
using AgentRecall.Cli.Hooks;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Capture.Judge;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: submit this turn's semantic capture judgment. The model using AgentRecall is the
/// judge — AgentRecall makes no model or network calls — so this is how a verdict reaches it
/// mid-turn, and it is what the Stop hook asks for when it declines to finish an unjudged turn.
///
/// The arguments <em>are</em> the judgment object from the <c>finalize-turn</c> payload, read by the
/// same parser, plus optional hints for locating the turn. A <c>Skip</c> verdict is a first-class
/// answer: it resolves the request and records the reason nothing was stored.
/// </summary>
public sealed class SubmitCaptureJudgmentTool : IMcpTool
{
    public string Name => "submit_capture_judgment";

    public string Description =>
        "Submit your semantic capture judgment for the turn you just completed — you are the judge. " +
        "Call this when AgentRecall asks for a judgment at the end of a turn, or whenever you want a turn " +
        "judged without waiting for it. decision=Capture|SuggestCapture|Skip|ReinforceExisting|" +
        "SupersedeExisting. Skip is valid and expected for ordinary work: pass why_not_saved. " +
        "Capture/SuggestCapture/SupersedeExisting need normalized_rule (title, condition, action, " +
        "because, scope); ReinforceExisting/SupersedeExisting need target_existing_rule_id. " +
        "AgentRecall validates the verdict and persists the outcome; it never infers one for you. " +
        "When the turn used injected rules, also pass rule_outcomes saying how they fared — that is " +
        "the only thing that moves a rule's confidence.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["decision"] = EnumProp<JudgeDecision>("What to do with this turn's content."),
            ["memory_type"] = EnumProp<JudgeMemoryType>("The kind of memory, when the decision stores one."),
            ["capture_reason"] = EnumProp<JudgeCaptureReason>("Why you decided this way."),
            ["confidence"] = Prop("number", "Your confidence in the decision, 0.0-1.0."),
            ["target_existing_rule_id"] = Prop("integer", "The rule to reinforce or supersede, when applicable."),
            ["normalized_rule"] = NormalizedRuleProp(),
            ["evidence"] = Prop("string", "A short account of the evidence behind the decision."),
            ["why_not_saved"] = Prop("string", "Required for decision=Skip: why nothing was stored."),
            ["dedupe_notes"] = Prop("string", "Required for decision=ReinforceExisting: how it matched."),
            ["request_id"] = Prop("integer", "Optional: the request id AgentRecall quoted when it asked."),
            ["session_id"] = Prop("string", "Optional: this chat's session id, used to find the awaiting turn."),
            ["cwd"] = Prop("string", "Optional: the turn's working directory, used as a fallback."),
            ["turn_id"] = Prop("string", "Optional: the turn correlation id, when you know it."),
            ["rule_outcomes"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] =
                    "How the rules AgentRecall injected this turn actually fared — the other half of " +
                    "recall, and the only signal that moves a rule's confidence. One entry per rule: " +
                    "{rule_id (or retrieval_id, quoted from the injected block), outcome, note}. " +
                    "outcome is UserAccepted, UserRejected, CorrectionRepeated or RuleIgnored — only " +
                    "what you actually witnessed; a build or test result must come from whatever ran " +
                    "the command, not from you. RuleIgnored is the honest answer for a rule that did " +
                    "not apply.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["rule_id"] = Prop("integer", "The rule being reported on."),
                        ["retrieval_id"] = Prop("string", "The retrieval id from the injected context block."),
                        ["outcome"] = Prop("string", "UserAccepted | UserRejected | CorrectionRepeated | RuleIgnored."),
                        ["note"] = Prop("string", "A short note recorded as the outcome's reason."),
                    },
                },
            },
            ["prompt"] = Prop("string", "Only for a turn that was not blocked: the user's message for it."),
            ["assistant_response"] = Prop("string", "Only for a turn that was not blocked: what you did/said."),
        },
        // memory_type is required in practice: it defaults to NotMemory when absent, which turns a
        // Capture verdict into a skip. Skip verdicts pass NotMemory explicitly.
        ["required"] = new JsonArray { "decision", "capture_reason", "memory_type" },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        // The arguments object is the judgment object, parsed by the same tolerant reader the
        // Stop-hook payload uses. A malformed verdict is rejected outright — there is no fallback
        // decision to make, and inventing one is exactly what this path exists to prevent.
        var verdict = TurnPayload.ParseVerdict(arguments, Console.Error);
        if (verdict is null)
        {
            return new JsonObject
            {
                ["submitted"] = false,
                ["reason"] =
                    "Could not read a valid judgment. 'decision' must be one of " +
                    $"{string.Join(", ", Enum.GetNames<JudgeDecision>())} and 'capture_reason' one of " +
                    $"{string.Join(", ", Enum.GetNames<JudgeCaptureReason>())}. Nothing was recorded; resubmit the verdict.",
            };
        }

        var ruleOutcomes = TurnPayload.ParseRuleOutcomes(arguments?["rule_outcomes"], Console.Error);
        var cwd = McpArgs.GetString(arguments, "cwd");
        var gate = services.GetRequiredService<ITurnJudgmentGate>();
        var result = await gate.SubmitAsync(
            new JudgmentSubmission
            {
                Verdict = verdict,
                RequestId = McpArgs.GetInt(arguments, "request_id"),
                SessionId = McpArgs.GetString(arguments, "session_id"),
                Cwd = cwd,
                TurnId = McpArgs.GetString(arguments, "turn_id"),
                Prompt = McpArgs.GetString(arguments, "prompt"),
                AssistantResponse = McpArgs.GetString(arguments, "assistant_response"),
                ScopeLevel = cwd is null ? ScopeLevel.Global : ScopeLevel.Repository,
                ScopeValue = TurnPayload.RepositoryScopeName(cwd),
                RuleOutcomes = ruleOutcomes,
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Submitted)
        {
            return new JsonObject
            {
                ["submitted"] = false,
                ["reason"] = result.Reason,
            };
        }

        await RecordActivityAsync(result, services, cancellationToken).ConfigureAwait(false);

        // Outcomes ride the verdict: the same helper the end-of-turn payload uses, so a report
        // counts the same whether it arrived through this tool or through finalize-turn.
        var outcomeReport = await Core.Outcomes.TurnOutcomeReporting.ApplyAndRecordAsync(
            services.GetRequiredService<Core.Outcomes.ITurnOutcomeReporter>(),
            services.GetRequiredService<IActivityRecorder>(),
            result.Finalization?.TurnId,
            ruleOutcomes,
            "mcp",
            cancellationToken).ConfigureAwait(false);

        return ToNode(result, outcomeReport);
    }

    private static async Task RecordActivityAsync(
        JudgmentSubmissionResult result, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (result.Finalization is not { } finalization)
        {
            return;
        }

        var notice = ActivityNoticeFactory.ForTurnFinalized(finalization, "mcp");
        if (notice is null)
        {
            return;
        }

        await services.GetRequiredService<IActivityRecorder>()
            .RecordAsync(notice with { TurnId = finalization.TurnId }, cancellationToken)
            .ConfigureAwait(false);
    }

    private static JsonNode ToNode(
        JudgmentSubmissionResult result, Core.Outcomes.TurnOutcomeReportResult outcomes)
    {
        var node = new JsonObject
        {
            ["submitted"] = true,
            ["request_id"] = result.RequestId,
            ["was_unprompted"] = result.WasUnprompted,
        };

        // Refusals are reported back, never swallowed: a reporter that believes it moved a rule's
        // confidence when it did not will keep reporting the same unusable outcome.
        if (!outcomes.IsEmpty)
        {
            node["outcomes_recorded"] = new JsonArray(
                [.. outcomes.Applied.Select(a => (JsonNode)$"#{a.RuleId} {a.Outcome}")]);
            node["outcomes_refused"] = new JsonArray([.. outcomes.Rejected.Select(r => (JsonNode)r)]);
            node["rules_awaiting_outcome"] = new JsonArray(
                [.. outcomes.Unreported.Select(id => (JsonNode)id)]);
        }

        if (result.Finalization is not { } finalization)
        {
            return node;
        }

        node["decision"] = finalization.Decision;
        node["decision_source"] = finalization.DecisionSource;
        node["confidence"] = finalization.JudgeConfidence;
        node["turn_id"] = finalization.TurnId;
        node["finalization_id"] = finalization.Id;
        node["captured_rule_ids"] = ToIdArray(finalization.Captured.Select(l => l.RuleId));
        node["suggested_rule_ids"] = ToIdArray(finalization.Suggested.Select(l => l.RuleId));
        node["summary"] = TurnFinalizationFormatter.SummaryLine(finalization);
        return node;
    }

    private static JsonArray ToIdArray(IEnumerable<int> ids)
    {
        var array = new JsonArray();
        foreach (var id in ids)
        {
            array.Add(id);
        }

        return array;
    }

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private static JsonObject EnumProp<T>(string description) where T : struct, Enum
    {
        var prop = Prop("string", description);
        var values = new JsonArray();
        foreach (var name in Enum.GetNames<T>())
        {
            values.Add(name);
        }

        prop["enum"] = values;
        return prop;
    }

    private static JsonObject NormalizedRuleProp() => new()
    {
        ["type"] = "object",
        ["description"] = "The rule distilled from the turn, in normalized parts.",
        ["properties"] = new JsonObject
        {
            ["title"] = Prop("string", "A short title for the rule."),
            ["condition"] = Prop("string", "When the rule applies (\"when …\")."),
            ["action"] = Prop("string", "What to do."),
            ["avoid"] = Prop("string", "The anti-pattern to avoid."),
            ["because"] = Prop("string", "Why it matters."),
            ["scope"] = Prop("string", "The scope it belongs to (e.g. a repository name)."),
            ["always_apply"] = Prop("boolean", "True for a universal constraint that applies to every task."),
            ["tags"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Optional tags.",
            },
        },
    };
}
