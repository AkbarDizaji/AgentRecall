using System.Globalization;
using System.Text;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Finalization;

/// <summary>
/// Renders a <see cref="TurnFinalizationResult"/> for humans. The single source of
/// truth for finalization wording, so the CLI, the Stop-hook notice, and the MCP
/// status tool all answer "did AgentRecall capture anything?" identically — and the
/// answer comes from the recorded decision, never from a guess.
/// </summary>
public static class TurnFinalizationFormatter
{
    /// <summary>The message shown when no finalization has been recorded for a turn.</summary>
    public const string NoFinalization =
        "No finalized AgentRecall capture is recorded for the last turn.";

    /// <summary>Decision-source label recorded when the semantic judge produced a verdict.</summary>
    public const string JudgeDecisionSource = TurnFinalizer.JudgeDecisionSource;

    /// <summary>
    /// The line reported while a turn's judgment is still outstanding. A blocked turn has no
    /// finalization yet, so without this the status surfaces would answer with the previous turn's
    /// outcome — the one thing they exist to prevent.
    /// </summary>
    public static string AwaitingJudgmentLine(int? requestId)
    {
        var request = requestId is { } id ? $" (request #{id})" : string.Empty;
        return
            $"AgentRecall asked for this turn's semantic capture judgment{request} and is still waiting for it. " +
            "Call `submit_capture_judgment` with your verdict — Skip is valid — and the turn is finalized from it.";
    }

    /// <summary>The multi-line, sectioned summary used by `finalize-turn` and `status`.</summary>
    public static string RenderText(TurnFinalizationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsEmpty)
        {
            return "No lessons found.";
        }

        var sb = new StringBuilder();
        sb.Append("AgentRecall finalized turn.");

        if (result.DecisionSource == JudgeDecisionSource && !string.IsNullOrEmpty(result.Decision))
        {
            sb.Append("\n\nDecision source: Semantic capture judge");
            sb.Append($"\nDecision: {result.Decision}");
            if (!string.IsNullOrEmpty(result.JudgeReason))
            {
                var confidence = result.JudgeConfidence is { } c
                    ? $", confidence: {c.ToString("0.00", CultureInfo.InvariantCulture)}"
                    : string.Empty;
                sb.Append($" (reason: {result.JudgeReason}{confidence})");
            }
        }

        if (result.Captured.Count > 0)
        {
            sb.Append("\n\nCaptured:");
            foreach (var lesson in result.Captured)
            {
                var standing = lesson.AlwaysApply ? " [standing — applies every turn]" : string.Empty;
                sb.Append($"\n- #{lesson.RuleId} {CategoryLabel(lesson.Category)}: {lesson.Text}{standing}");
                if (lesson.AwaitingApproval)
                {
                    sb.Append($"\n  Awaiting your approval — reply yes/no, or run `agentrecall rules approve {lesson.RuleId}` " +
                              $"to remember it, or `agentrecall rules archive {lesson.RuleId}` to discard it.");
                }
            }
        }

        if (result.Skipped.Count > 0)
        {
            sb.Append("\n\nSkipped:");
            foreach (var skip in result.Skipped)
            {
                sb.Append($"\n- {skip.Reason}");
            }
        }

        if (result.Suggested.Count > 0)
        {
            sb.Append("\n\nSuggested:");
            foreach (var lesson in result.Suggested)
            {
                var note = string.IsNullOrWhiteSpace(lesson.Note) ? string.Empty : $" ({lesson.Note})";
                sb.Append($"\n- #{lesson.RuleId} Pending rule: {lesson.Text}{note}");
                sb.Append($"\n  Run `agentrecall rules approve {lesson.RuleId}` to remember it, " +
                          $"or `agentrecall rules archive {lesson.RuleId}` to ignore it.");
            }
        }

        if (result.Errors.Count > 0)
        {
            sb.Append("\n\nErrors:");
            foreach (var error in result.Errors)
            {
                sb.Append($"\n- {error}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// A one-line answer to "did AgentRecall capture anything?", in the exact phrasing
    /// the agent should echo: captured / suggested-pending / skipped / nothing recorded.
    /// </summary>
    public static string SummaryLine(TurnFinalizationResult? result)
    {
        if (result is null || result.IsEmpty)
        {
            return NoFinalization;
        }

        if (result.Captured.Count > 0)
        {
            var lesson = result.Captured[0];
            var extra = result.Captured.Count > 1 ? $" (+{result.Captured.Count - 1} more)" : string.Empty;
            if (lesson.AwaitingApproval)
            {
                return $"AgentRecall captured rule #{lesson.RuleId}: {Summarize(lesson.Text)}.{extra} " +
                       $"Awaiting your approval — reply yes/no, or run `agentrecall rules approve {lesson.RuleId}` to remember it.";
            }

            return $"AgentRecall captured rule #{lesson.RuleId}: {Summarize(lesson.Text)}.{extra}";
        }

        if (result.Suggested.Count > 0)
        {
            var lesson = result.Suggested[0];
            return $"AgentRecall suggested pending rule #{lesson.RuleId}: {Summarize(lesson.Text)}. " +
                   $"Run `agentrecall rules approve {lesson.RuleId}` to remember it.";
        }

        var reinforced = result.Skipped.FirstOrDefault(s => s.DuplicateOfRuleId is not null);
        if (reinforced is not null)
        {
            return $"AgentRecall reinforced existing rule #{reinforced.DuplicateOfRuleId} (no new rule).";
        }

        if (result.Skipped.Count > 0)
        {
            return $"AgentRecall skipped capture: {result.Skipped[0].Reason}";
        }

        return NoFinalization;
    }

    /// <summary>Maps a rule category to the label used in finalization output.</summary>
    public static string CategoryLabel(RuleCategory category) => category switch
    {
        RuleCategory.RepositoryConvention => "Repository rule",
        RuleCategory.EngineeringLesson => "Engineering lesson",
        RuleCategory.CodeFact => "Code fact",
        _ => "Rule",
    };

    private static string Summarize(string text)
    {
        // Trim trailing sentence punctuation; the caller supplies a single period.
        var trimmed = (text ?? string.Empty).Trim().TrimEnd('.', '!', '?', ' ');
        return trimmed.Length <= 100 ? trimmed : trimmed[..99] + "…";
    }
}
