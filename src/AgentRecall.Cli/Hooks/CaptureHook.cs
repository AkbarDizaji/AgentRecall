using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Finalization;
using AgentRecall.Core.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Hooks;

/// <summary>
/// Runs the deterministic capture path, intended for a Claude Code <c>Stop</c> hook
/// (which fires after the assistant finishes a turn). It reads the hook payload,
/// extracts the latest user correction from the turn (inline, or by parsing the
/// transcript the payload points to), and — only when the message reads as a
/// reusable correction — routes it through <see cref="IFeedbackService"/>, which
/// applies the same memory-worthiness policy as every other capture flow.
///
/// It never throws: any failure is logged to the supplied diagnostics writer and the
/// hook reports nothing, so Claude Code is never blocked. It returns a short,
/// user-facing message when (and only when) an actual capture decision was made, or
/// <c>null</c> when there is nothing to say (no correction, duplicate, disabled).
/// </summary>
public static class CaptureHook
{
    /// <summary>Tag applied to every rule learned automatically by the capture hook.</summary>
    public const string SourceTag = "auto-capture";

    public static async Task<string?> RunAsync(
        string? hookInputJson,
        IServiceProvider services,
        TextWriter diagnostics,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = services.GetRequiredService<AgentRecallOptions>();
            if (!options.CaptureHookEnabled || string.IsNullOrWhiteSpace(hookInputJson))
            {
                return null;
            }

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(hookInputJson);
            }
            catch (JsonException)
            {
                return null;
            }

            if (root is null)
            {
                return null;
            }

            var (userText, assistantText) = ExtractTurns(root, diagnostics);
            if (string.IsNullOrWhiteSpace(userText))
            {
                return null;
            }

            // Only genuine corrections reach the classifier; ordinary task prompts and
            // questions are filtered out here so normal work is never captured.
            var analyzer = services.GetRequiredService<IFeedbackCandidateAnalyzer>();
            if (!analyzer.Analyze(userText).IsCandidate)
            {
                return null;
            }

            var accepted = (root["accepted"]?.GetValue<bool>() ?? false) || HasAcceptanceIntent(userText);
            var repository = RepositoryName(root["cwd"]?.GetValue<string>());

            // Outcome-aware evidence from the turn (an observed failure, a user correction,
            // an accepted review, a test that failed then passed, a repeat). When present,
            // the adaptive worthiness policy weighs it; when absent, capture is unchanged.
            var outcome = services.GetRequiredService<ITurnCandidateExtractor>()
                .DetectOutcomeSignals(userText, assistantText);
            var context = outcome.HasAny
                ? new CaptureContext
                {
                    Source = SourceTag,
                    AcceptanceSignal = accepted,
                    ExplicitSaveRequest = accepted,
                    ObservedFailure = outcome.ObservedFailure,
                    UserCorrection = outcome.UserCorrection,
                    ReviewAccepted = outcome.ReviewAccepted,
                    TestFailedThenFixed = outcome.TestFailedThenFixed,
                    RepeatedCorrectionCount = outcome.RepeatedCorrectionCount,
                    EvidenceSummary = BuildEvidenceSummary(outcome, userText),
                }
                : null;

            var input = new FeedbackInput
            {
                Task = BuildTask(assistantText, repository),
                Feedback = userText.Trim(),
                ScopeLevel = repository is null ? ScopeLevel.Global : ScopeLevel.Repository,
                ScopeValue = repository,
                Tags = SourceTag,
                // Accepted review guidance is forced Active; a plain correction follows
                // the configured default. The worthiness classifier still gates both.
                AutoApprove = accepted ? true : null,
                Context = context,
            };

            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
                .InitializeAsync(cancellationToken).ConfigureAwait(false);

            var feedback = scope.ServiceProvider.GetRequiredService<IFeedbackService>();
            var result = await feedback.AddAsync(input, cancellationToken).ConfigureAwait(false);

            // Record the capture decision for the human-visible activity log.
            var notice = ActivityNoticeFactory.ForFeedback(result, "stop_hook");
            if (notice is not null)
            {
                await scope.ServiceProvider.GetRequiredService<IActivityRecorder>()
                    .RecordAsync(notice, cancellationToken).ConfigureAwait(false);
            }

            return Describe(result);
        }
        catch (Exception ex)
        {
            // Never block the turn; surface the failure on stderr only.
            diagnostics.WriteLine($"[agentrecall] capture skipped: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds the user-facing message from the capture decision. AgentRecall has
    /// already decided; this only reports it. AutoCapture and SuggestCapture each
    /// notify (the latter naming the one narrow action left to the user); a Skip that
    /// reinforced a duplicate changes nothing visible, so it stays silent.
    /// </summary>
    private static string? Describe(FeedbackResult result)
    {
        var decision = result.Decision;
        if (decision is null)
        {
            // Defensive fallback for the legacy shape (no decision computed).
            return result.Rule is null ? null : $"AgentRecall captured rule:\n{result.Rule.RuleText}";
        }

        switch (decision.Outcome)
        {
            case CaptureOutcome.Skip:
                // A duplicate reinforcement is silent; a not-worthy code fact is surfaced
                // so the user knows why nothing was kept.
                return result.ReusedExistingRule
                    ? null
                    : $"AgentRecall skipped capture: {decision.Reason}";

            case CaptureOutcome.SuggestCapture:
            {
                var actions = result.Rule is { } pending
                    ? $"Confirm with `agentrecall rules approve {pending.Id}` or drop it with `agentrecall rules archive {pending.Id}`."
                    : "Confirm it with `agentrecall rules approve <id>`.";
                var idLabel = result.Rule is { } r ? $" #{r.Id}" : string.Empty;
                return
                    $"AgentRecall found a possible {decision.ScopeLabel} rule but the evidence is ambiguous " +
                    $"(confidence {decision.Confidence:0.00}); saved it as a pending suggestion{idLabel} for review.\n" +
                    $"Reason: {decision.Reason}\n" +
                    $"Notice: {decision.Notice}\n" +
                    actions;
            }

            default: // AutoCapture
            {
                // Preserve the "captured rule" / "captured generalized lesson" wording
                // callers and tests key on, then add the decision rationale.
                var lead = result.Worthiness?.Verdict == MemoryWorthiness.NeedsReview
                    ? "AgentRecall captured generalized lesson"
                    : "AgentRecall captured rule";
                return
                    $"✓ {lead} ({decision.ScopeLabel}, confidence {decision.Confidence:0.00}).\n" +
                    $"Reason: {decision.Reason}\n" +
                    $"Notice: {decision.Notice}";
            }
        }
    }

    /// <summary>
    /// Resolves the latest user correction and assistant response for the turn,
    /// preferring inline fields and falling back to the referenced transcript file.
    /// </summary>
    private static (string? UserText, string? AssistantText) ExtractTurns(JsonNode root, TextWriter diagnostics)
    {
        var inlinePrompt = root["prompt"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(inlinePrompt))
        {
            return (inlinePrompt, root["assistant_response"]?.GetValue<string>());
        }

        var transcriptPath = root["transcript_path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath))
        {
            return (null, null);
        }

        try
        {
            return ReadTranscript(transcriptPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.WriteLine($"[agentrecall] capture could not read transcript: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Parses a Claude Code JSONL transcript and returns the last user turn's text and
    /// the last assistant turn's text. Lines that don't parse, or user turns that carry
    /// only tool results (no prose), are ignored.
    /// </summary>
    private static (string? UserText, string? AssistantText) ReadTranscript(string path)
    {
        string? lastUser = null;
        string? lastAssistant = null;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? entry;
            try
            {
                entry = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            var type = entry?["type"]?.GetValue<string>();
            if (type is not ("user" or "assistant"))
            {
                continue;
            }

            var text = ExtractMessageText(entry!["message"]);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (type == "user")
            {
                lastUser = text;
            }
            else
            {
                lastAssistant = text;
            }
        }

        return (lastUser, lastAssistant);
    }

    /// <summary>
    /// Concatenates the text content of a transcript message. Message content is
    /// either a plain string or an array of blocks; only <c>text</c> blocks contribute
    /// (tool calls, tool results, and thinking are skipped).
    /// </summary>
    private static string? ExtractMessageText(JsonNode? message)
    {
        var content = message?["content"];
        if (content is null)
        {
            return null;
        }

        if (content is JsonValue value && value.TryGetValue<string>(out var direct))
        {
            return direct;
        }

        if (content is not JsonArray blocks)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            if (block?["type"]?.GetValue<string>() != "text")
            {
                continue;
            }

            var blockText = block["text"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(blockText))
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(blockText);
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string BuildEvidenceSummary(TurnOutcomeSignals outcome, string userText)
    {
        var parts = new List<string>();
        if (outcome.ObservedFailure) parts.Add("the agent's output broke or changed behaviour");
        if (outcome.UserCorrection) parts.Add("the user corrected it");
        if (outcome.ReviewAccepted) parts.Add("a review comment was applied");
        if (outcome.TestFailedThenFixed) parts.Add("a test failed then passed");
        if (outcome.RepeatedCorrectionCount >= 2) parts.Add("the same correction recurred");

        var evidence = parts.Count > 0 ? string.Join("; ", parts) : "an observed outcome";
        var snippet = userText.Trim();
        if (snippet.Length > 120)
        {
            snippet = snippet[..119] + "…";
        }

        return $"Observed in turn: {evidence}. Correction: {snippet}";
    }

    private static bool HasAcceptanceIntent(string text) =>
        ReviewAcceptanceIntent.Matches(text);

    private static string BuildTask(string? assistantText, string? repository)
    {
        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            // Keep the task short; it only provides a recall cue, not the full reply.
            var trimmed = assistantText.Trim();
            return trimmed.Length <= 160 ? trimmed : trimmed[..159] + "…";
        }

        return repository is null ? "agent correction" : $"working in {repository}";
    }

    /// <summary>Repository name = the nearest ancestor with a .git, else the cwd's name.</summary>
    private static string? RepositoryName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return null;
        }

        try
        {
            var dir = new DirectoryInfo(cwd);
            for (var current = dir; current is not null; current = current.Parent)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                {
                    return current.Name;
                }
            }

            return dir.Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
