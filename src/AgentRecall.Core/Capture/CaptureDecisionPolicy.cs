using AgentRecall.Core.Configuration;

namespace AgentRecall.Core.Capture;

/// <summary>
/// Deterministic <see cref="ICaptureDecisionPolicy"/>. It is the single place that
/// turns the upstream signals into a capture decision, so the choice to store, ask,
/// or skip is made inside AgentRecall rather than left to the agent.
///
/// The rules are evaluated in order; the first match wins:
/// <list type="number">
///   <item>A duplicate is skipped (the existing rule is reinforced, not re-stored).</item>
///   <item>A low-value code fact is skipped, unless accepted guidance is allowed to
///   override the filter — in which case it is captured.</item>
///   <item>A worthy lesson the user explicitly accepted is captured immediately.</item>
///   <item>A worthy lesson held back for review (approve posture off) is suggested.</item>
///   <item>A worthy lesson whose confidence meets the auto bar is captured; below it,
///   the lesson is suggested rather than acted on.</item>
/// </list>
/// </summary>
public sealed class CaptureDecisionPolicy : ICaptureDecisionPolicy
{
    private readonly AgentRecallOptions _options;

    public CaptureDecisionPolicy(AgentRecallOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public CaptureDecision Decide(CaptureSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        // 1. Nothing new to store: an equivalent rule already exists. The caller still
        //    reinforces it with a fresh event; the decision itself is a silent skip.
        if (signals.IsDuplicate)
        {
            return Skip(
                signals,
                "An equivalent rule already exists; reinforced it instead of storing a duplicate.",
                notice: "an equivalent rule already existed, so the existing rule was reinforced");
        }

        // 2. Not a reusable lesson — a low-value code fact recoverable from the repo.
        if (!signals.Worthy)
        {
            if (signals.ApprovePosture && signals.CodeFactOverrideAllowed)
            {
                return AutoCapture(
                    signals,
                    "captured because you accepted this guidance, which overrides the code-fact filter");
            }

            return Skip(signals, signals.WorthinessReason, notice: string.Empty);
        }

        // 3. A worthy lesson. Decide auto vs. suggest.

        // 3a. The user explicitly accepted it (accepted PR comment, "apply the review",
        //     explicit approve) — the strongest signal. Capture without asking.
        if (signals.ExplicitAcceptance)
        {
            return AutoCapture(signals, "captured automatically because the acceptance signal was strong");
        }

        // 3b. Held back for review (approve posture off, or the caller forced it).
        //     Park it as a suggestion rather than acting unilaterally.
        if (!signals.ApprovePosture)
        {
            return SuggestCapture(
                signals,
                "auto-approve is off, so this was parked as a pending suggestion for you to confirm");
        }

        // 3c. No explicit acceptance, posture on: confidence decides.
        var bar = _options.CaptureAutoConfidence;
        if (signals.Confidence >= bar)
        {
            return AutoCapture(
                signals,
                $"captured automatically because confidence ({signals.Confidence:0.00}) met the auto-capture bar ({bar:0.00})");
        }

        return SuggestCapture(
            signals,
            $"confidence ({signals.Confidence:0.00}) is below the auto-capture bar ({bar:0.00}), so it was parked as a pending suggestion");
    }

    private static CaptureDecision AutoCapture(CaptureSignals s, string notice) =>
        new(CaptureOutcome.AutoCapture, s.WorthinessReason, s.Confidence, s.ScopeLevel, s.ScopeValue, notice);

    private static CaptureDecision SuggestCapture(CaptureSignals s, string notice) =>
        new(CaptureOutcome.SuggestCapture, s.WorthinessReason, s.Confidence, s.ScopeLevel, s.ScopeValue, notice);

    private static CaptureDecision Skip(CaptureSignals s, string reason, string notice) =>
        new(CaptureOutcome.Skip, reason, s.Confidence, s.ScopeLevel, s.ScopeValue, notice);
}
