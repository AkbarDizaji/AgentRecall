namespace AgentRecall.Core.Capture.Judge;

/// <summary>The outcome of validating a judge verdict's structure.</summary>
/// <param name="IsValid">True when the verdict is structurally sound for its decision.</param>
/// <param name="Reason">Why it was rejected, when it was.</param>
/// <param name="DowngradeToSuggest">
/// True when an otherwise-capturable verdict is missing only a non-core field, so the mapper
/// may store it as a pending suggestion rather than skipping it outright.
/// </param>
public readonly record struct CaptureJudgeValidation(bool IsValid, string Reason, bool DowngradeToSuggest)
{
    /// <summary>A passing validation.</summary>
    public static readonly CaptureJudgeValidation Valid = new(true, string.Empty, false);

    /// <summary>A failing validation carrying the reason.</summary>
    public static CaptureJudgeValidation Invalid(string reason, bool downgradeToSuggest = false) =>
        new(false, reason, downgradeToSuggest);
}

/// <summary>
/// Deterministic, offline validation of a <see cref="CaptureJudgeVerdict"/>. It enforces only
/// the mechanical contract — required fields per decision, confidence range, bounded lengths,
/// and a guard against raw turn text leaking in as a rule. It makes no semantic judgement
/// (that is the model's job): a structurally sound verdict is passed through to the mapper.
///
/// An invalid verdict never throws; it maps to a skip (or, when only a non-core field is
/// missing, a downgrade to a pending suggestion). There is never a keyword fallback.
/// </summary>
public static class CaptureJudgeValidator
{
    /// <summary>Maximum length of a rule title.</summary>
    public const int TitleMaxLength = 160;

    /// <summary>Maximum length of any other normalized rule field.</summary>
    public const int FieldMaxLength = 2000;

    /// <summary>Maximum number of tags.</summary>
    public const int MaxTags = 10;

    /// <summary>Maximum length of a single tag.</summary>
    public const int TagMaxLength = 60;

    /// <summary>Validates a verdict against its decision's structural contract.</summary>
    public static CaptureJudgeValidation Validate(CaptureJudgeVerdict verdict, CaptureJudgeInput? input = null)
    {
        if (double.IsNaN(verdict.Confidence) || verdict.Confidence < 0.0 || verdict.Confidence > 1.0)
        {
            return CaptureJudgeValidation.Invalid("confidence out of range");
        }

        var rule = verdict.NormalizedRule;

        // Length/prose guards apply to any rule the verdict carries, whatever the decision.
        if (rule is not null)
        {
            var lengths = CheckLengths(rule);
            if (lengths is not null)
            {
                return CaptureJudgeValidation.Invalid(lengths);
            }

            if (LooksLikeRawTurnText(rule, input))
            {
                return CaptureJudgeValidation.Invalid("normalized rule echoes raw turn text");
            }
        }

        switch (verdict.Decision)
        {
            case JudgeDecision.Capture:
                if (!IsMinimallyStorable(rule))
                {
                    return CaptureJudgeValidation.Invalid("capture is missing title/condition/action");
                }

                if (!IsSound(rule))
                {
                    // Core guidance is present but a rationale/scope is missing: recoverable as a
                    // pending suggestion rather than an active capture.
                    return CaptureJudgeValidation.Invalid("capture is missing because/scope", downgradeToSuggest: true);
                }

                return CaptureJudgeValidation.Valid;

            case JudgeDecision.SupersedeExisting:
                if (verdict.TargetExistingRuleId is null)
                {
                    return CaptureJudgeValidation.Invalid("supersede is missing target_existing_rule_id");
                }

                return IsSound(rule)
                    ? CaptureJudgeValidation.Valid
                    : CaptureJudgeValidation.Invalid("supersede is missing a sound normalized_rule");

            case JudgeDecision.SuggestCapture:
                return IsMinimallyStorable(rule)
                    ? CaptureJudgeValidation.Valid
                    : CaptureJudgeValidation.Invalid("suggestion is missing title/condition/action");

            case JudgeDecision.ReinforceExisting:
                if (verdict.TargetExistingRuleId is null)
                {
                    return CaptureJudgeValidation.Invalid("reinforce is missing target_existing_rule_id");
                }

                return string.IsNullOrWhiteSpace(verdict.DedupeNotes)
                    ? CaptureJudgeValidation.Invalid("reinforce is missing dedupe_notes")
                    : CaptureJudgeValidation.Valid;

            case JudgeDecision.Skip:
                return string.IsNullOrWhiteSpace(verdict.WhyNotSaved)
                    ? CaptureJudgeValidation.Invalid("skip is missing why_not_saved")
                    : CaptureJudgeValidation.Valid;

            default:
                return CaptureJudgeValidation.Invalid("unknown decision");
        }
    }

    /// <summary>All of title, condition, and action carry content — the minimum to store a rule.</summary>
    public static bool IsMinimallyStorable(NormalizedRule? rule) =>
        rule is not null &&
        !string.IsNullOrWhiteSpace(rule.Title) &&
        !string.IsNullOrWhiteSpace(rule.Condition) &&
        !string.IsNullOrWhiteSpace(rule.Action);

    /// <summary>A fully-formed rule: condition, action, rationale, and scope are all present.</summary>
    public static bool IsSound(NormalizedRule? rule) =>
        IsMinimallyStorable(rule) &&
        !string.IsNullOrWhiteSpace(rule!.Because) &&
        !string.IsNullOrWhiteSpace(rule.Scope);

    private static string? CheckLengths(NormalizedRule rule)
    {
        if (Length(rule.Title) > TitleMaxLength)
        {
            return "title too long";
        }

        if (Length(rule.Condition) > FieldMaxLength ||
            Length(rule.Action) > FieldMaxLength ||
            Length(rule.Avoid) > FieldMaxLength ||
            Length(rule.Because) > FieldMaxLength ||
            Length(rule.Scope) > FieldMaxLength)
        {
            return "normalized rule field too long";
        }

        if (rule.Tags.Count > MaxTags || rule.Tags.Any(t => Length(t) > TagMaxLength))
        {
            return "too many or over-long tags";
        }

        return null;
    }

    // A rule whose action or title is just the assistant's raw response verbatim is prose that
    // leaked in, not a distilled rule. We only compare against the assistant summary: the user's
    // own correction frequently *is* the rule, so echoing the user prompt is legitimate.
    private static bool LooksLikeRawTurnText(NormalizedRule rule, CaptureJudgeInput? input)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.AssistantSummary))
        {
            return false;
        }

        return Matches(rule.Action, input.AssistantSummary) || Matches(rule.Title, input.AssistantSummary);
    }

    private static bool Matches(string? value, string turnText) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value.Trim(), turnText.Trim(), StringComparison.OrdinalIgnoreCase);

    private static int Length(string? value) => value?.Trim().Length ?? 0;
}
