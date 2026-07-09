using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Capture.Judge;

/// <summary>
/// Maps a validated <see cref="CaptureJudgeVerdict"/> to a deterministic
/// <see cref="CaptureJudgeOutcome"/>. This is the confidence-threshold and reason policy that
/// runs <b>after</b> <see cref="CaptureJudgeValidator"/>. It is pure and offline; it never
/// re-judges the turn, and it never falls back to keyword-driven capture.
///
/// Precedence (first match wins): an invalid verdict skips (or downgrades to a suggestion when
/// only a non-core field was missing); an explicit do-not-save always skips; an explicit save
/// of a sound rule always captures; the judge's Skip/Reinforce/Supersede decisions are honored;
/// read-only/non-memory reasons skip; a code fact skips; otherwise the confidence bands decide
/// capture (≥0.80) vs suggest (0.55–0.79) vs skip (&lt;0.55).
/// </summary>
public static class CaptureJudgeDecisionMapper
{
    /// <summary>Confidence at or above which a valid verdict is auto-captured.</summary>
    public const double CaptureThreshold = 0.80;

    /// <summary>Confidence at or above which a valid verdict is suggested (below it is skipped).</summary>
    public const double SuggestThreshold = 0.55;

    // Reasons that mark read-only source material or non-memory: always skipped unless the user
    // explicitly asked to save (handled earlier).
    private static readonly HashSet<JudgeCaptureReason> SkipReasons =
    [
        JudgeCaptureReason.SourceDocumentOnly,
        JudgeCaptureReason.AssistantProse,
        JudgeCaptureReason.CommandOutputOnly,
        JudgeCaptureReason.LogOutputOnly,
        JudgeCaptureReason.NotMemory,
        JudgeCaptureReason.NotReusable,
        JudgeCaptureReason.Ambiguous,
    ];

    /// <summary>Maps a verdict + its validation result to the action AgentRecall will take.</summary>
    public static CaptureJudgeOutcome Map(CaptureJudgeVerdict verdict, CaptureJudgeValidation validation)
    {
        var judgeReason = verdict.CaptureReason.ToString();
        var judgeDecision = verdict.Decision.ToString();

        CaptureJudgeOutcome Outcome(JudgePersistAction action, string reason, RuleStatus status = RuleStatus.Pending) =>
            new()
            {
                Action = action,
                Category = MapCategory(verdict.MemoryType),
                DomainReason = MapDomainReason(verdict.CaptureReason),
                JudgeReason = judgeReason,
                JudgeDecision = judgeDecision,
                Status = status,
                Confidence = verdict.Confidence,
                TargetRuleId = verdict.TargetExistingRuleId,
                Rule = verdict.NormalizedRule,
                Reason = reason,
            };

        // Invalid: skip, or downgrade a nearly-capturable verdict to a pending suggestion.
        if (!validation.IsValid)
        {
            return validation.DowngradeToSuggest && CaptureJudgeValidator.IsMinimallyStorable(verdict.NormalizedRule)
                ? Outcome(JudgePersistAction.Suggest, $"Downgraded to suggestion: {validation.Reason}.", RuleStatus.Pending)
                : Outcome(JudgePersistAction.Skip, $"Invalid judge output: {validation.Reason}.");
        }

        // An explicit do-not-save is an unconditional skip.
        if (verdict.CaptureReason == JudgeCaptureReason.ExplicitUserDoNotSave)
        {
            return Outcome(JudgePersistAction.Skip, "User explicitly asked not to save.");
        }

        // An explicit save of a sound rule always captures — even for a narrow, stylistic, or
        // code-fact rule (validation already required a sound rule with a rationale).
        if (verdict.CaptureReason == JudgeCaptureReason.ExplicitUserSave)
        {
            return CaptureJudgeValidator.IsSound(verdict.NormalizedRule)
                ? Outcome(JudgePersistAction.AutoCapture, "Explicit user save.", RuleStatus.Active)
                : Outcome(JudgePersistAction.Skip, "Explicit save without a storable rule.");
        }

        // Honor the judge's explicit non-capture decisions.
        if (verdict.Decision == JudgeDecision.Skip)
        {
            return Outcome(JudgePersistAction.Skip, verdict.WhyNotSaved ?? "Not memory-worthy.");
        }

        if (verdict.Decision == JudgeDecision.ReinforceExisting ||
            verdict.CaptureReason == JudgeCaptureReason.DuplicateExisting)
        {
            return Outcome(JudgePersistAction.Reinforce,
                $"Reinforced existing rule #{verdict.TargetExistingRuleId}.");
        }

        if (verdict.Decision == JudgeDecision.SupersedeExisting)
        {
            return Outcome(JudgePersistAction.Supersede,
                $"Supersedes rule #{verdict.TargetExistingRuleId}.", RuleStatus.Active);
        }

        // Read-only source material / non-memory.
        if (SkipReasons.Contains(verdict.CaptureReason))
        {
            return Outcome(JudgePersistAction.Skip, $"Not stored: {judgeReason}.");
        }

        // A code fact is recoverable by searching the repository (explicit save handled above).
        if (verdict.MemoryType == JudgeMemoryType.CodeFact || verdict.CaptureReason == JudgeCaptureReason.CodeFact)
        {
            return Outcome(JudgePersistAction.Skip, "Code fact, recoverable from the repository.");
        }

        // Otherwise the confidence bands decide.
        if (verdict.Confidence >= CaptureThreshold)
        {
            return Outcome(JudgePersistAction.AutoCapture, "Captured on judge confidence.", RuleStatus.Active);
        }

        if (verdict.Confidence >= SuggestThreshold)
        {
            return Outcome(JudgePersistAction.Suggest, "Suggested for review on judge confidence.", RuleStatus.Pending);
        }

        return Outcome(JudgePersistAction.Skip, "Below the capture confidence threshold.");
    }

    /// <summary>Maps the judge's memory type to the domain rule category.</summary>
    public static RuleCategory MapCategory(JudgeMemoryType type) => type switch
    {
        JudgeMemoryType.EngineeringLesson => RuleCategory.EngineeringLesson,
        JudgeMemoryType.ReviewLesson => RuleCategory.EngineeringLesson,
        JudgeMemoryType.DocBackedCorrection => RuleCategory.EngineeringLesson,
        JudgeMemoryType.RepositoryConvention => RuleCategory.RepositoryConvention,
        JudgeMemoryType.ToolWorkflowConvention => RuleCategory.RepositoryConvention,
        JudgeMemoryType.UserPreference => RuleCategory.UserPreference,
        JudgeMemoryType.CommunicationPreference => RuleCategory.CommunicationPreference,
        JudgeMemoryType.CodeFact => RuleCategory.CodeFact,
        _ => RuleCategory.Unknown,
    };

    /// <summary>Maps the judge's reason to the nearest domain capture reason for the stored rule.</summary>
    public static CaptureReason MapDomainReason(JudgeCaptureReason reason) => reason switch
    {
        JudgeCaptureReason.ExplicitUserSave => CaptureReason.ManualFeedback,
        JudgeCaptureReason.ObservedAgentFailure => CaptureReason.ObservedAgentFailure,
        JudgeCaptureReason.ReviewerCorrection => CaptureReason.AcceptedReviewComment,
        JudgeCaptureReason.UserCorrection => CaptureReason.UserCorrection,
        JudgeCaptureReason.RepeatedMistake => CaptureReason.RepeatedCorrection,
        JudgeCaptureReason.UserPreference => CaptureReason.ExplicitUserPreference,
        JudgeCaptureReason.DocBackedCorrection => CaptureReason.ObservedAgentFailure,
        _ => CaptureReason.None,
    };
}
