using AgentRecall.Core.Configuration;
using AgentRecall.Core.Memory;

namespace AgentRecall.Core.Capture;

/// <summary>
/// Deterministic <see cref="IAdaptiveWorthinessPolicy"/>. It layers outcome-aware
/// evidence on top of the text-only capture decision so worthiness depends not only on
/// what a candidate says, but on what produced it:
/// <list type="bullet">
///   <item>A bare code fact is never auto-captured, even after a failure (at most a
///   review suggestion when the user explicitly asks to save it).</item>
///   <item>Generic best practice with no observed failure is skipped.</item>
///   <item>Generic best practice backed by an observed failure, a user correction, an
///   accepted review, or a repeat is captured or suggested.</item>
///   <item>A repeated correction raises confidence and strongly favours capture.</item>
///   <item>An explicit do-not-save skips; an explicit save can capture a worthy
///   low-confidence lesson.</item>
///   <item>A duplicate reinforces the existing rule; a conflict is held for review.</item>
/// </list>
/// Same inputs, same output — no LLM, no embeddings, no randomness.
/// </summary>
public sealed class AdaptiveWorthinessPolicy : IAdaptiveWorthinessPolicy
{
    private readonly AgentRecallOptions _options;

    public AdaptiveWorthinessPolicy(AgentRecallOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public AdaptiveWorthinessResult Adjust(
        MemoryWorthinessResult? worthiness,
        CaptureContext context,
        CaptureDecision baseDecision,
        bool isDuplicate,
        bool conflictExists)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(baseDecision);

        var conflict = conflictExists || context.ConflictExists;
        var isCodeFact = worthiness is { Verdict: MemoryWorthiness.NotWorthStoring };
        var repeated = context.RepeatedCorrectionCount >= 2 || context.PriorSimilarMistakeCount >= 1;
        var reason = ResolveReason(context, repeated);
        var confidence = AdjustConfidence(worthiness?.Confidence ?? baseDecision.Confidence, context);

        // G. An explicit "do not save" wins, unless the user also explicitly asked to save.
        if (context.ExplicitDoNotSave && !context.ExplicitSaveRequest)
        {
            return new AdaptiveWorthinessResult(
                CaptureOutcome.Skip, confidence, CaptureReason.None,
                "Honoured an explicit do-not-save instruction.");
        }

        // H. A duplicate stores nothing new; the caller reinforces the existing rule.
        if (isDuplicate)
        {
            return new AdaptiveWorthinessResult(
                CaptureOutcome.Skip, confidence, reason,
                "An equivalent rule already exists; reinforced it with this new evidence.");
        }

        // A & D. A bare code fact is recoverable from the repository, so it is never
        // auto-captured even when a failure was observed — at most parked for review on
        // an explicit save request.
        if (isCodeFact)
        {
            return context.ExplicitSaveRequest
                ? new AdaptiveWorthinessResult(
                    CaptureOutcome.SuggestCapture, confidence, reason,
                    "Code fact parked for review only because you explicitly asked to save it.")
                : new AdaptiveWorthinessResult(
                    CaptureOutcome.Skip, confidence, CaptureReason.None,
                    "A bare code fact is recoverable from the repository; not captured even though an outcome was observed.");
        }

        // A worthy lesson. A positive capture driver is an observed outcome, an explicit
        // save, or an acceptance signal.
        var hasDriver = context.HasOutcomeEvidence || context.ExplicitSaveRequest || context.AcceptanceSignal;

        // B. Generic best practice with no observed failure: skip.
        if (!hasDriver)
        {
            return new AdaptiveWorthinessResult(
                CaptureOutcome.Skip, confidence, CaptureReason.None,
                "Generic best practice with no observed failure; nothing captured.");
        }

        // I. A conflict with an existing active rule is held for review rather than
        // auto-captured, since the resolution is not decisive here.
        if (conflict)
        {
            return new AdaptiveWorthinessResult(
                CaptureOutcome.SuggestCapture, confidence, reason,
                "Conflicts with an existing rule, so it was parked as a pending suggestion rather than auto-captured.");
        }

        // C, E, F, J. A driver is present. Acceptance, an accepted review, an explicit
        // save, a repeat, or sufficient confidence makes the decision decisive.
        var decisive =
            context.AcceptanceSignal || context.ReviewAccepted || context.ExplicitSaveRequest ||
            repeated || confidence >= _options.CaptureAutoConfidence;

        return decisive
            ? new AdaptiveWorthinessResult(
                CaptureOutcome.AutoCapture, confidence, reason, BuildCaptureExplanation(reason, confidence))
            : new AdaptiveWorthinessResult(
                CaptureOutcome.SuggestCapture, confidence, reason,
                $"Worthy lesson backed by {Describe(reason)}, parked as a pending suggestion to confirm (confidence {confidence:0.00}).");
    }

    /// <summary>
    /// Picks the capture reason from the strongest available evidence. Evidence-derived
    /// reasons win over a source-declared one so an imported, accepted review still reads
    /// as <see cref="CaptureReason.AcceptedReviewComment"/>.
    /// </summary>
    private static CaptureReason ResolveReason(CaptureContext context, bool repeated)
    {
        if (context.ReviewAccepted) return CaptureReason.AcceptedReviewComment;
        if (repeated) return CaptureReason.RepeatedCorrection;
        if (context.ObservedFailure) return CaptureReason.ObservedAgentFailure;
        if (context.TestFailedThenFixed) return CaptureReason.TestFailedThenFixed;
        if (context.UserCorrection) return CaptureReason.UserCorrection;
        if (context.Reason != CaptureReason.None) return context.Reason;
        return SourceReason(context.Source);
    }

    private static CaptureReason SourceReason(string? source)
    {
        var s = (source ?? string.Empty).ToLowerInvariant();
        if (s.Contains("min", StringComparison.Ordinal)) return CaptureReason.LessonMined;
        if (s.Contains("import", StringComparison.Ordinal) || s.Contains("pr", StringComparison.Ordinal) ||
            s.Contains("pull", StringComparison.Ordinal) || s.Contains("log", StringComparison.Ordinal))
        {
            return CaptureReason.ImportedFeedback;
        }

        return CaptureReason.ManualFeedback;
    }

    /// <summary>Raises confidence on outcome evidence; repeats raise it the most.</summary>
    private static double AdjustConfidence(double baseConfidence, CaptureContext context)
    {
        var c = baseConfidence;
        if (context.ObservedFailure) c += 0.15;
        if (context.UserCorrection) c += 0.10;
        if (context.ReviewAccepted) c += 0.15;
        if (context.TestFailedThenFixed) c += 0.15;

        var repeats = Math.Max(0, context.RepeatedCorrectionCount - 1) + Math.Max(0, context.PriorSimilarMistakeCount);
        c += Math.Min(0.30, 0.10 * repeats);

        return Math.Round(Math.Clamp(c, 0.0, 1.0), 2);
    }

    private static string BuildCaptureExplanation(CaptureReason reason, double confidence) =>
        $"Captured because of {Describe(reason)} (confidence {confidence:0.00}).";

    private static string Describe(CaptureReason reason) => reason switch
    {
        CaptureReason.ObservedAgentFailure => "an observed agent failure",
        CaptureReason.UserCorrection => "a user correction",
        CaptureReason.AcceptedReviewComment => "an accepted review comment",
        CaptureReason.TestFailedThenFixed => "a test that failed then passed",
        CaptureReason.RepeatedCorrection => "a repeated correction",
        CaptureReason.LessonMined => "a mined repeated lesson",
        CaptureReason.ImportedFeedback => "imported feedback",
        _ => "the supplied feedback",
    };
}
