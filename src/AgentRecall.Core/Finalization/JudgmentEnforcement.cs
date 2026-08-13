namespace AgentRecall.Core.Finalization;

/// <summary>
/// How strictly the Stop-hook path insists on a semantic capture judgment before it lets a turn
/// finish. The judge is always the session model — enforcement changes only whether AgentRecall
/// accepts silence as an answer.
/// </summary>
public enum JudgmentEnforcementMode
{
    /// <summary>
    /// Never block. A turn with no supplied judgment is finalized as unjudged and recorded as
    /// such. This is the pre-enforcement behaviour, kept as the escape hatch.
    /// </summary>
    Off = 0,

    /// <summary>
    /// The default. Block a turn that is substantive — one carrying both a prompt and an assistant
    /// response of at least the configured combined size — and ask the model for its verdict.
    /// The size floor is structural, not semantic: it never inspects wording, so it can only
    /// exempt a trivially short exchange, never decide what is worth remembering.
    /// </summary>
    Substantive = 1,

    /// <summary>Block any turn that carries a prompt, however short.</summary>
    Always = 2,
}

/// <summary>What the Stop-hook path should do with a turn, once enforcement has been considered.</summary>
public enum JudgmentEnforcementAction
{
    /// <summary>Finalize now: a judgment is present, or none is required for this turn.</summary>
    Finalize = 0,

    /// <summary>Ask the model for its verdict and do not let the turn finish yet.</summary>
    RequestJudgment = 1,

    /// <summary>
    /// Finalize without a judgment because asking again is not allowed (the allowed asks are used
    /// up, or the host says this Stop is already a resumption). The turn is recorded as unjudged
    /// with the exhausted reason, never as though a judge service were down.
    /// </summary>
    ProceedUnjudged = 2,
}

/// <summary>
/// The facts the enforcement decision is made from. Deliberately free of host shapes and of any
/// content signal beyond size, so the decision stays deterministic and reviewable.
/// </summary>
public sealed record JudgmentEnforcementFacts
{
    /// <summary>True when the payload carried a parsed, structurally valid verdict.</summary>
    public bool HasSuppliedJudgment { get; init; }

    /// <summary>True when this turn was already judged (e.g. the model submitted before Stop fired).</summary>
    public bool AlreadyJudged { get; init; }

    /// <summary>True when the turn carries a non-empty user prompt.</summary>
    public bool HasPrompt { get; init; }

    /// <summary>True when the turn carries a non-empty assistant response.</summary>
    public bool HasAssistantResponse { get; init; }

    /// <summary>Combined prompt + response length, compared against the structural size floor.</summary>
    public int TurnCharacters { get; init; }

    /// <summary>How many times this turn has already been asked for a judgment.</summary>
    public int PriorAttempts { get; init; }

    /// <summary>
    /// True when the host signalled that this Stop is already the resumption of a blocked one.
    /// Optional: the current Claude Code payload does not document such a field, so the attempt
    /// counter — not this flag — is the loop guard. When a host does send it, it forces the
    /// no-further-blocking path.
    /// </summary>
    public bool HostSaysResumed { get; init; }
}

/// <summary>The enforcement decision, with the reason it was reached.</summary>
/// <param name="Action">What the Stop-hook path should do.</param>
/// <param name="Reason">Why, for the recorded diagnostics.</param>
public readonly record struct JudgmentEnforcementDecision(JudgmentEnforcementAction Action, string Reason);

/// <summary>
/// Decides whether a turn may finish without a semantic capture judgment. Pure and total: no IO,
/// no clock, no content inspection beyond the structural size floor — so "would this turn be
/// blocked?" is answerable in a unit test and identical every time.
///
/// It is not a capture decision and cannot become one: it never chooses what to remember, only
/// whether the model still owes AgentRecall a verdict. Skipping straight to a keyword classifier
/// when the model stays silent is exactly what this path exists to prevent.
/// </summary>
public static class JudgmentEnforcementPolicy
{
    /// <summary>Default combined prompt + response characters before a turn counts as substantive.</summary>
    public const int DefaultMinTurnCharacters = 200;

    /// <summary>Default number of times one turn may be blocked for its judgment.</summary>
    public const int DefaultMaxAttempts = 1;

    /// <summary>
    /// How long a turn's recorded judgment state (an ask, or a verdict) stays attached to that turn.
    /// A turn correlation id is derived from the prompt, so an identical prompt typed much later
    /// must not inherit the earlier turn's verdict or its exhausted attempts.
    /// </summary>
    public const int TurnJudgmentFreshnessMinutes = 60;

    /// <summary>Recorded when a judgment was present and the turn finalized straight away.</summary>
    public const string JudgmentPresentReason = "A semantic capture judgment was supplied for this turn.";

    /// <summary>Recorded when the turn was already judged before this Stop fired.</summary>
    public const string AlreadyJudgedReason = "This turn was already judged; the recorded verdict stands.";

    /// <summary>Recorded when enforcement is switched off.</summary>
    public const string EnforcementOffReason = "Judgment enforcement is off; the turn finalizes unjudged.";

    /// <summary>Recorded when the turn is too small to be worth asking about.</summary>
    public const string NotSubstantiveReason = "The turn is below the substantive-turn size floor; no judgment was requested.";

    /// <summary>Recorded when a judgment is being requested.</summary>
    public const string RequestingReason = "No semantic capture judgment was supplied; asking the session model for one.";

    /// <summary>Recorded when the allowed asks are used up.</summary>
    public const string AttemptsExhaustedReason =
        "AgentRecall already asked for this turn's judgment and the turn resumed without one; finalizing unjudged.";

    /// <summary>Recorded when the host says this Stop is already a resumption.</summary>
    public const string HostResumedReason =
        "The host reports this Stop is already a resumption; finalizing unjudged rather than asking again.";

    /// <summary>Applies the policy to one turn.</summary>
    public static JudgmentEnforcementDecision Decide(
        JudgmentEnforcementFacts facts,
        JudgmentEnforcementMode mode,
        int minTurnCharacters = DefaultMinTurnCharacters,
        int maxAttempts = DefaultMaxAttempts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // A verdict in hand — supplied now or recorded earlier for the same turn — ends it.
        if (facts.HasSuppliedJudgment)
        {
            return new JudgmentEnforcementDecision(JudgmentEnforcementAction.Finalize, JudgmentPresentReason);
        }

        if (facts.AlreadyJudged)
        {
            return new JudgmentEnforcementDecision(JudgmentEnforcementAction.Finalize, AlreadyJudgedReason);
        }

        if (mode == JudgmentEnforcementMode.Off)
        {
            return new JudgmentEnforcementDecision(JudgmentEnforcementAction.Finalize, EnforcementOffReason);
        }

        if (!IsWorthAsking(facts, mode, minTurnCharacters))
        {
            return new JudgmentEnforcementDecision(JudgmentEnforcementAction.Finalize, NotSubstantiveReason);
        }

        // Loop guards, in order of authority: the host's own resumption signal when it sends one,
        // then AgentRecall's persisted attempt counter, which works whether it sends one or not.
        if (facts.HostSaysResumed)
        {
            return new JudgmentEnforcementDecision(JudgmentEnforcementAction.ProceedUnjudged, HostResumedReason);
        }

        if (facts.PriorAttempts >= Math.Max(0, maxAttempts))
        {
            return new JudgmentEnforcementDecision(JudgmentEnforcementAction.ProceedUnjudged, AttemptsExhaustedReason);
        }

        return new JudgmentEnforcementDecision(JudgmentEnforcementAction.RequestJudgment, RequestingReason);
    }

    private static bool IsWorthAsking(JudgmentEnforcementFacts facts, JudgmentEnforcementMode mode, int minTurnCharacters) =>
        mode switch
        {
            // Always still needs something to judge: a turn with no prompt carries no exchange.
            JudgmentEnforcementMode.Always => facts.HasPrompt,
            JudgmentEnforcementMode.Substantive =>
                facts.HasPrompt &&
                facts.HasAssistantResponse &&
                facts.TurnCharacters >= Math.Max(0, minTurnCharacters),
            _ => false,
        };
}
