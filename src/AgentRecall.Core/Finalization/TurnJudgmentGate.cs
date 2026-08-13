using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Capture.Judge;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Finalization;

/// <summary>
/// The instruction AgentRecall hands back when it declines to finish an unjudged turn. It is the
/// only channel available — a blocked Stop carries one string — so it names the tool, the decision
/// vocabulary, and the required fields, and it says plainly that a rejection is a valid answer.
/// Kept bounded and in one place so the CLI, the tests, and the docs cannot drift apart.
/// </summary>
public static class JudgmentBlockMessage
{
    /// <summary>Builds the block instruction, quoting the request id when there is one.</summary>
    public static string For(int? requestId)
    {
        var request = requestId is { } id ? $" (request #{id})" : string.Empty;
        return
            $"AgentRecall needs this turn's semantic capture judgment before the turn can finish{request}. " +
            "You are the judge: AgentRecall makes no model or network calls of its own, so a turn nobody " +
            "judges is a turn it must record as unjudged. Call the `submit_capture_judgment` MCP tool now " +
            "with your verdict for the turn you just completed:\n" +
            "- decision: Capture | SuggestCapture | Skip | ReinforceExisting | SupersedeExisting\n" +
            "- memory_type, confidence, capture_reason\n" +
            "- normalized_rule (title, condition, action, because, scope) for Capture/SuggestCapture/SupersedeExisting\n" +
            "- target_existing_rule_id for ReinforceExisting/SupersedeExisting\n" +
            "- why_not_saved for Skip\n" +
            "Skip is a valid and expected answer: most turns hold nothing durable, and a reported Skip is " +
            "real signal. Do not redo the work and do not ask the user — submit the verdict, then finish.";
    }
}

/// <summary>
/// Default <see cref="ITurnJudgmentGate"/>. It decides whether a turn still owes AgentRecall a
/// verdict (<see cref="JudgmentEnforcementPolicy"/> makes that call, deterministically), records
/// the ask, and later finalizes the turn from the verdict that answers it.
///
/// Two properties matter more than anything else here. It fails open: any error while evaluating
/// ends in "finalize", never in "block", because a block AgentRecall cannot record is a block it
/// could re-emit forever. And it never substitutes its own judgment — when the model stays silent
/// the turn is finalized as unjudged and recorded as such, not handed to a keyword classifier.
/// </summary>
public sealed class TurnJudgmentGate : ITurnJudgmentGate
{
    /// <summary>
    /// How long a recorded ask stays relevant to the turn that raised it. A blocked turn resumes
    /// within one model round-trip, so anything older is debris from a chat that ended mid-exchange
    /// (or a byte-identical prompt from much earlier) and must not silently suppress enforcement.
    /// </summary>
    internal const int RequestFreshnessMinutes = JudgmentEnforcementPolicy.TurnJudgmentFreshnessMinutes;

    /// <summary>Cap on the turn text copied onto a request row, so one huge turn cannot bloat the table.</summary>
    internal const int MaxStoredTextLength = 20_000;

    /// <summary>Recorded when enforcement itself failed and the turn was let through unblocked.</summary>
    public const string EnforcementFailedReason =
        "Judgment enforcement could not run, so the turn was finalized without blocking.";

    private readonly ITurnJudgmentRequestRepository _requests;
    private readonly ITurnFinalizationRepository _finalizations;
    private readonly ITurnFinalizer _finalizer;
    private readonly AgentRecallOptions _options;

    public TurnJudgmentGate(
        ITurnJudgmentRequestRepository requests,
        ITurnFinalizationRepository finalizations,
        ITurnFinalizer finalizer,
        AgentRecallOptions options)
    {
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _finalizations = finalizations ?? throw new ArgumentNullException(nameof(finalizations));
        _finalizer = finalizer ?? throw new ArgumentNullException(nameof(finalizer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<JudgmentGateDecision> EvaluateAsync(
        TurnFinalizationInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // A supplied verdict needs no state at all, so answer it before touching the database.
        if (input.SuppliedJudgment is not null)
        {
            return new JudgmentGateDecision
            {
                Action = JudgmentEnforcementAction.Finalize,
                Reason = JudgmentEnforcementPolicy.JudgmentPresentReason,
            };
        }

        try
        {
            var turnId = TurnCorrelation.Compute(input.Cwd, input.Prompt) ?? string.Empty;
            var request = await FindLiveRequestAsync(input, turnId, cancellationToken).ConfigureAwait(false);

            var facts = new JudgmentEnforcementFacts
            {
                HasSuppliedJudgment = false,
                AlreadyJudged = await IsAlreadyJudgedAsync(request, turnId, cancellationToken).ConfigureAwait(false),
                HasPrompt = !string.IsNullOrWhiteSpace(input.Prompt),
                HasAssistantResponse = !string.IsNullOrWhiteSpace(input.AssistantResponse),
                TurnCharacters = (input.Prompt?.Length ?? 0) + (input.AssistantResponse?.Length ?? 0),
                PriorAttempts = request?.Status == JudgmentRequestStatus.Outstanding ? request.Attempts : 0,
                HostSaysResumed = input.HostResumedTurn,
            };

            var decision = JudgmentEnforcementPolicy.Decide(
                facts,
                _options.ResolvedJudgmentEnforcementMode,
                _options.JudgmentEnforcementMinTurnCharacters,
                _options.MaxJudgmentRequestsPerTurn);

            return decision.Action switch
            {
                JudgmentEnforcementAction.RequestJudgment =>
                    await RecordRequestAsync(input, turnId, request, decision.Reason, cancellationToken).ConfigureAwait(false),
                JudgmentEnforcementAction.ProceedUnjudged =>
                    await AbandonAsync(request, decision.Reason, cancellationToken).ConfigureAwait(false),
                _ => new JudgmentGateDecision
                {
                    Action = JudgmentEnforcementAction.Finalize,
                    Reason = decision.Reason,
                    RequestId = request?.Id,
                    Attempts = request?.Attempts ?? 0,
                },
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail open. Blocking depends on being able to record the ask; if that failed, asking
            // again could repeat forever, so the turn finalizes (unjudged, and recorded as such).
            return new JudgmentGateDecision
            {
                Action = JudgmentEnforcementAction.Finalize,
                Reason = $"{EnforcementFailedReason} ({ex.Message})",
            };
        }
    }

    public async Task<JudgmentSubmissionResult> SubmitAsync(
        JudgmentSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var request = await ResolveTargetAsync(submission, cancellationToken).ConfigureAwait(false);

        // No request outstanding: a verdict volunteered without a block still finalizes a turn,
        // provided the caller says which turn it is about.
        if (request is null)
        {
            if (string.IsNullOrWhiteSpace(submission.Prompt))
            {
                return new JudgmentSubmissionResult
                {
                    Submitted = false,
                    Reason =
                        "No turn is awaiting a judgment, and the submission carried no prompt to finalize. " +
                        "Pass 'prompt' (and ideally 'assistant_response') to judge a turn that was not blocked.",
                };
            }

            var unpromptedResult = await _finalizer.FinalizeAsync(
                new TurnFinalizationInput
                {
                    Cwd = submission.Cwd,
                    Prompt = submission.Prompt,
                    AssistantResponse = submission.AssistantResponse,
                    Source = submission.Source,
                    SessionId = submission.SessionId,
                    ScopeLevel = submission.ScopeLevel,
                    ScopeValue = submission.ScopeValue,
                    SuppliedJudgment = submission.Verdict,
                },
                cancellationToken).ConfigureAwait(false);

            return new JudgmentSubmissionResult
            {
                Submitted = true,
                WasUnprompted = true,
                Finalization = unpromptedResult,
            };
        }

        var result = await _finalizer.FinalizeAsync(ToInput(request, submission.Verdict), cancellationToken)
            .ConfigureAwait(false);

        request.Status = JudgmentRequestStatus.Resolved;
        request.ResolvedAt = DateTimeOffset.UtcNow;
        request.ResolvedDecision = submission.Verdict.Decision.ToString();
        request.FinalizationId = result.Id;
        await _requests.UpdateAsync(request, cancellationToken).ConfigureAwait(false);

        return new JudgmentSubmissionResult
        {
            Submitted = true,
            RequestId = request.Id,
            Finalization = result,
        };
    }

    public Task<TurnJudgmentRequest?> FindOutstandingAsync(
        string? sessionId, string? cwd, CancellationToken cancellationToken = default) =>
        _requests.FindOutstandingAsync(sessionId, cwd, cancellationToken);

    /// <summary>
    /// The request that belongs to this turn: the outstanding one for the chat when it is still
    /// fresh, else a fresh (or answered) one recorded against the same turn id. Stale debris is
    /// closed rather than trusted, so an abandoned chat cannot mute enforcement for the next turn.
    /// </summary>
    private async Task<TurnJudgmentRequest?> FindLiveRequestAsync(
        TurnFinalizationInput input, string turnId, CancellationToken cancellationToken)
    {
        var outstanding = await _requests
            .FindOutstandingAsync(input.SessionId, input.Cwd, cancellationToken).ConfigureAwait(false);

        if (outstanding is not null)
        {
            if (IsFresh(outstanding.CreatedAt))
            {
                return outstanding;
            }

            outstanding.Status = JudgmentRequestStatus.Abandoned;
            outstanding.ResolvedAt = DateTimeOffset.UtcNow;
            await _requests.UpdateAsync(outstanding, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(turnId))
        {
            return null;
        }

        var byTurn = await _requests.FindByTurnAsync(turnId, cancellationToken).ConfigureAwait(false);
        return byTurn is not null && IsFresh(byTurn.CreatedAt) ? byTurn : null;
    }

    /// <summary>
    /// True when this turn already carries a real verdict — the model answered the ask, or it
    /// self-reported a judgment before Stop fired. Only recent evidence counts, for the same reason
    /// stale requests are ignored: a turn id is derived from the prompt, so an identical prompt from
    /// a much earlier session must not inherit its verdict.
    /// </summary>
    private async Task<bool> IsAlreadyJudgedAsync(
        TurnJudgmentRequest? request, string turnId, CancellationToken cancellationToken)
    {
        if (request?.Status == JudgmentRequestStatus.Resolved)
        {
            return true;
        }

        if (string.IsNullOrEmpty(turnId))
        {
            return false;
        }

        var judged = await _finalizations.FindJudgedByTurnAsync(turnId, cancellationToken).ConfigureAwait(false);
        return judged is not null && IsFresh(judged.CreatedAt);
    }

    private async Task<JudgmentGateDecision> RecordRequestAsync(
        TurnFinalizationInput input,
        string turnId,
        TurnJudgmentRequest? existing,
        string reason,
        CancellationToken cancellationToken)
    {
        TurnJudgmentRequest request;
        if (existing is { Status: JudgmentRequestStatus.Outstanding })
        {
            existing.Attempts++;
            request = await _requests.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            request = await _requests.AddAsync(
                new TurnJudgmentRequest
                {
                    TurnId = turnId,
                    SessionId = input.SessionId ?? string.Empty,
                    Cwd = input.Cwd ?? string.Empty,
                    Source = input.Source,
                    Prompt = Bound(input.Prompt),
                    AssistantResponse = Bound(input.AssistantResponse),
                    ScopeLevel = input.ScopeLevel,
                    ScopeValue = input.ScopeValue ?? string.Empty,
                    Attempts = 1,
                    Status = JudgmentRequestStatus.Outstanding,
                },
                cancellationToken).ConfigureAwait(false);
        }

        return new JudgmentGateDecision
        {
            Action = JudgmentEnforcementAction.RequestJudgment,
            Reason = reason,
            RequestId = request.Id,
            Attempts = request.Attempts,
            BlockReason = JudgmentBlockMessage.For(request.Id),
        };
    }

    private async Task<JudgmentGateDecision> AbandonAsync(
        TurnJudgmentRequest? request, string reason, CancellationToken cancellationToken)
    {
        if (request is { Status: JudgmentRequestStatus.Outstanding })
        {
            request.Status = JudgmentRequestStatus.Abandoned;
            request.ResolvedAt = DateTimeOffset.UtcNow;
            await _requests.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return new JudgmentGateDecision
        {
            Action = JudgmentEnforcementAction.ProceedUnjudged,
            Reason = reason,
            RequestId = request?.Id,
            Attempts = request?.Attempts ?? 0,
        };
    }

    private async Task<TurnJudgmentRequest?> ResolveTargetAsync(
        JudgmentSubmission submission, CancellationToken cancellationToken)
    {
        if (submission.RequestId is { } id)
        {
            var byId = await _requests.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (byId is { Status: JudgmentRequestStatus.Outstanding })
            {
                return byId;
            }
        }

        if (!string.IsNullOrEmpty(submission.TurnId))
        {
            var byTurn = await _requests.FindByTurnAsync(submission.TurnId, cancellationToken).ConfigureAwait(false);
            if (byTurn is { Status: JudgmentRequestStatus.Outstanding })
            {
                return byTurn;
            }
        }

        return await _requests
            .FindOutstandingAsync(submission.SessionId, submission.Cwd, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rebuilds the turn from the request row. Using the stored text — not whatever the resumed
    /// transcript now says — keeps the finalized turn identical to the one that was blocked, so the
    /// idempotency hash and the turn correlation id both stay put.
    /// </summary>
    private static TurnFinalizationInput ToInput(TurnJudgmentRequest request, CaptureJudgeVerdict verdict) =>
        new()
        {
            Cwd = request.Cwd,
            Prompt = request.Prompt,
            AssistantResponse = request.AssistantResponse,
            Source = string.IsNullOrEmpty(request.Source) ? "stop_hook" : request.Source,
            SessionId = string.IsNullOrEmpty(request.SessionId) ? null : request.SessionId,
            ScopeLevel = request.ScopeLevel,
            ScopeValue = string.IsNullOrEmpty(request.ScopeValue) ? null : request.ScopeValue,
            SuppliedJudgment = verdict,
        };

    private static bool IsFresh(DateTimeOffset createdAt) =>
        DateTimeOffset.UtcNow - createdAt <= TimeSpan.FromMinutes(RequestFreshnessMinutes);

    private static string Bound(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= MaxStoredTextLength ? text : text[..MaxStoredTextLength];
    }
}
