namespace AgentRecall.Core.Finalization;

/// <summary>What the source/outcome-aware decision matrix concluded for a candidate.</summary>
public enum SourceCaptureAction
{
    /// <summary>Let the candidate proceed to the existing structure/quality gate.</summary>
    AllowToQualityGate,

    /// <summary>Keep the candidate out of memory; it is read-only source material or a do-not-save.</summary>
    Skip,
}

/// <summary>
/// The outcome of the source/outcome-aware decision matrix: whether the candidate may
/// proceed, the kind that decided it, and — when skipped — the structured skip reason.
/// </summary>
/// <param name="Action">Allow to the quality gate, or skip.</param>
/// <param name="Kind">The classified source/outcome kind.</param>
/// <param name="SkipReason">The skip reason, or <see cref="CaptureSkipReason.None"/> when allowed.</param>
/// <param name="Reason">The classifier's named reason, for diagnostics.</param>
public readonly record struct SourceCaptureVerdict(
    SourceCaptureAction Action,
    CandidateSourceKind Kind,
    CaptureSkipReason SkipReason,
    string Reason)
{
    /// <summary>True when the candidate must be kept out of memory.</summary>
    public bool ShouldSkip => Action == SourceCaptureAction.Skip;
}

/// <summary>
/// The source/outcome-aware decision matrix. It runs after <see cref="CandidateSourceClassifier"/>
/// and before the existing structure/quality gate. The rule is simple and deterministic:
///
/// <para>
/// Documentation, tool/skill instructions, command output, log lines, and assistant
/// meta-prose are read-only. On their own they are skipped. They may become memory only when
/// the turn pairs them with an <b>observed agent failure</b>, an <b>explicit save</b>, or a
/// <b>confirmed repository convention</b> — in which case the candidate is allowed through to
/// the quality gate (which still has the final say). An explicit do-not-save is always a hard
/// skip. Everything else (user/review feedback, corrections, repository confirmations) is
/// allowed straight through.
/// </para>
/// </summary>
public static class SourceAwareCaptureDecision
{
    /// <summary>
    /// Decides a candidate from its classification and the turn's outcome-aware pairing signals.
    /// </summary>
    /// <param name="classification">The candidate's classified kind and reason.</param>
    /// <param name="pairedWithObservedFailure">The turn carries an observed failure or user correction.</param>
    /// <param name="pairedWithExplicitSave">The turn carries an explicit save/acceptance.</param>
    /// <param name="pairedWithRepositoryConfirmation">The turn confirms a repository convention.</param>
    public static SourceCaptureVerdict Decide(
        CandidateClassification classification,
        bool pairedWithObservedFailure,
        bool pairedWithExplicitSave,
        bool pairedWithRepositoryConfirmation)
    {
        var kind = classification.Kind;
        var reason = classification.Reason;

        // An explicit do-not-save is an unconditional hard skip — no pairing can override it.
        if (kind == CandidateSourceKind.UserExplicitDoNotSave)
        {
            return Skip(kind, CaptureSkipReason.ExplicitDoNotSave, reason);
        }

        var paired = pairedWithObservedFailure || pairedWithExplicitSave || pairedWithRepositoryConfirmation;

        return kind switch
        {
            CandidateSourceKind.SourceDocumentInstruction =>
                paired ? Allow(kind, reason) : Skip(kind, CaptureSkipReason.SourceDocument, reason),
            CandidateSourceKind.ToolOrSkillInstruction =>
                paired ? Allow(kind, reason) : Skip(kind, CaptureSkipReason.ToolOrSkillInstruction, reason),
            CandidateSourceKind.CommandOutput =>
                paired ? Allow(kind, reason) : Skip(kind, CaptureSkipReason.CommandOutput, reason),
            CandidateSourceKind.LogOutput =>
                paired ? Allow(kind, reason) : Skip(kind, CaptureSkipReason.LogOutput, reason),

            // Meta-prose only earns its way in on an explicit save or an observed failure; a
            // repository confirmation elsewhere in the turn is not enough to keep chatter.
            CandidateSourceKind.AssistantMetaProse =>
                pairedWithExplicitSave || pairedWithObservedFailure
                    ? Allow(kind, reason)
                    : Skip(kind, CaptureSkipReason.AssistantProse, reason),

            // UserFeedback, UserExplicitSave, ReviewFeedback, ObservedAgentFailure,
            // RepositoryConventionConfirmation, Unknown — all proceed to the quality gate.
            _ => Allow(kind, reason),
        };
    }

    private static SourceCaptureVerdict Allow(CandidateSourceKind kind, string reason) =>
        new(SourceCaptureAction.AllowToQualityGate, kind, CaptureSkipReason.None, reason);

    private static SourceCaptureVerdict Skip(CandidateSourceKind kind, CaptureSkipReason skipReason, string reason) =>
        new(SourceCaptureAction.Skip, kind, skipReason, reason);
}
