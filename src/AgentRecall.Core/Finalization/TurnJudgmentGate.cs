using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Capture.Judge;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Finalization;

/// <summary>
/// The instruction AgentRecall hands back when it declines to finish an unjudged turn. It is the
/// only channel available — a blocked Stop carries one string — but that string is not private:
/// Claude Code prints it into the user's transcript, under a prefix of its own that reads
/// "Stop hook error". That prefix is the host's and cannot be changed here, so the message opens by
/// saying nothing went wrong: the user sees the word "error" for a routine handoff and would
/// otherwise think they broke something. It stays one short paragraph for the same reason. The
/// vocabulary and the required fields live where the model reads them anyway (the
/// <c>submit_capture_judgment</c> schema and the project instructions); repeating them here only
/// bought the user a wall of text. Kept in one place so the CLI, the tests, and the docs cannot
/// drift apart.
/// </summary>
public static class JudgmentBlockMessage
{
    /// <summary>Builds the block instruction, quoting the request id when there is one.</summary>
    public static string For(int? requestId)
    {
        var request = requestId is { } id ? $" (request #{id})" : string.Empty;
        return
            $"Nothing went wrong — AgentRecall is asking for this turn's capture judgment{request}. " +
            "Call `submit_capture_judgment`, then finish. Skip (with why_not_saved) is the expected " +
            "answer for ordinary work. Do not redo the work and do not ask the user.";
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

    /// <summary>Recorded when automatic capture is off, so a verdict would have nowhere to go.</summary>
    public const string CaptureDisabledReason =
        "Automatic capture is disabled, so no judgment was requested for this turn.";

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

        // Nothing would be done with a verdict when automatic capture is switched off, so asking for
        // one would cost the user a blocked turn and buy nothing.
        if (!_options.TurnFinalizerEnabled || _options.ResolvedCaptureJudgeMode == CaptureJudgeMode.Off)
        {
            return new JudgmentGateDecision
            {
                Action = JudgmentEnforcementAction.Finalize,
                Reason = CaptureDisabledReason,
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

        // A named request that is already settled is refused rather than retargeted: a repeated tool
        // call must not silently attach its verdict to some other turn's outstanding ask.
        if (submission.RequestId is { } named)
        {
            var byId = await _requests.GetAsync(named, cancellationToken).ConfigureAwait(false);
            if (byId is not null && byId.Status != JudgmentRequestStatus.Outstanding)
            {
                return new JudgmentSubmissionResult
                {
                    Submitted = false,
                    RequestId = named,
                    Reason =
                        $"Request #{named} is already {byId.Status.ToString().ToLowerInvariant()}; " +
                        "its turn has been finalized and nothing further was recorded.",
                };
            }
        }

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

    public async Task CloseOutstandingAsync(
        TurnFinalizationInput input, TurnFinalizationResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);

        // Only a real verdict closes an ask. An unjudged finalization leaves it open on purpose:
        // the turn still owes AgentRecall a judgment.
        if (result.DecisionSource != TurnFinalizer.JudgeDecisionSource)
        {
            return;
        }

        try
        {
            var outstanding = await _requests
                .FindOutstandingAsync(input.SessionId, input.Cwd, cancellationToken).ConfigureAwait(false);

            // Match on the turn when both sides know it, so a judged turn cannot close the ask
            // raised for a different one.
            if (outstanding is null ||
                !IsFresh(outstanding.CreatedAt) ||
                (!string.IsNullOrEmpty(outstanding.TurnId) &&
                 !string.IsNullOrEmpty(result.TurnId) &&
                 !string.Equals(outstanding.TurnId, result.TurnId, StringComparison.Ordinal)))
            {
                return;
            }

            outstanding.Status = JudgmentRequestStatus.Resolved;
            outstanding.ResolvedAt = DateTimeOffset.UtcNow;
            outstanding.ResolvedDecision = result.Decision ?? string.Empty;
            outstanding.FinalizationId = result.Id;
            await _requests.UpdateAsync(outstanding, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Housekeeping only: the turn is already judged and recorded either way.
            Console.Error.WriteLine($"[agentrecall] could not close the judgment request: {ex.Message}");
        }
    }

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

    /// <summary>
    /// The request this verdict answers. An id the caller quotes is honoured as given; the looser
    /// hints (turn id, then chat/directory) only adopt an ask that is still fresh, so a verdict
    /// cannot be attached to debris from a chat that ended mid-exchange — which would finalize the
    /// wrong turn's text under the wrong turn id.
    /// </summary>
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
            if (byTurn is { Status: JudgmentRequestStatus.Outstanding } && IsFresh(byTurn.CreatedAt))
            {
                return byTurn;
            }
        }

        var outstanding = await _requests
            .FindOutstandingAsync(submission.SessionId, submission.Cwd, cancellationToken).ConfigureAwait(false);

        return outstanding is not null && IsFresh(outstanding.CreatedAt) ? outstanding : null;
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
