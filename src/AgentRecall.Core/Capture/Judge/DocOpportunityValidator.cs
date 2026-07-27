namespace AgentRecall.Core.Capture.Judge;

/// <summary>The outcome of validating a document-opportunity verdict's structure.</summary>
/// <param name="IsValid">True when the verdict is structurally sound for its decision.</param>
/// <param name="Reason">Why it was rejected, when it was.</param>
public readonly record struct DocOpportunityValidation(bool IsValid, string Reason)
{
    /// <summary>A passing validation.</summary>
    public static readonly DocOpportunityValidation Valid = new(true, string.Empty);

    /// <summary>A failing validation carrying the reason.</summary>
    public static DocOpportunityValidation Invalid(string reason) => new(false, reason);
}

/// <summary>
/// Deterministic, offline validation of a <see cref="DocOpportunityVerdict"/>. It enforces only
/// the mechanical contract — required fields per decision, confidence range, bounded lengths.
/// It makes no semantic judgement (that is the model's job): a structurally sound verdict is
/// passed through to the service; an invalid one never throws, it is simply not surfaced.
/// </summary>
public static class DocOpportunityValidator
{
    /// <summary>Maximum length of the suggested document title.</summary>
    public const int TitleMaxLength = 160;

    /// <summary>Maximum length of the reason/why-not-offered prose.</summary>
    public const int FieldMaxLength = 2000;

    /// <summary>Maximum number of key points.</summary>
    public const int MaxKeyPoints = 10;

    /// <summary>Maximum length of a single key point.</summary>
    public const int KeyPointMaxLength = 300;

    /// <summary>Validates a verdict against its decision's structural contract.</summary>
    public static DocOpportunityValidation Validate(DocOpportunityVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        if (double.IsNaN(verdict.Confidence) || verdict.Confidence < 0.0 || verdict.Confidence > 1.0)
        {
            return DocOpportunityValidation.Invalid("confidence out of range");
        }

        if (Length(verdict.SuggestedTitle) > TitleMaxLength)
        {
            return DocOpportunityValidation.Invalid("suggested_title too long");
        }

        if (Length(verdict.Reason) > FieldMaxLength || Length(verdict.WhyNotOffered) > FieldMaxLength)
        {
            return DocOpportunityValidation.Invalid("reason or why_not_offered too long");
        }

        if (verdict.KeyPoints.Count > MaxKeyPoints || verdict.KeyPoints.Any(p => Length(p) > KeyPointMaxLength))
        {
            return DocOpportunityValidation.Invalid("too many or over-long key points");
        }

        return verdict.Decision switch
        {
            DocOpportunityDecision.Offer => string.IsNullOrWhiteSpace(verdict.SuggestedTitle)
                ? DocOpportunityValidation.Invalid("offer is missing suggested_title")
                : !Enum.IsDefined(verdict.DocumentType)
                    ? DocOpportunityValidation.Invalid("offer has an undefined document_type")
                    : DocOpportunityValidation.Valid,

            DocOpportunityDecision.Skip => string.IsNullOrWhiteSpace(verdict.WhyNotOffered)
                ? DocOpportunityValidation.Invalid("skip is missing why_not_offered")
                : DocOpportunityValidation.Valid,

            _ => DocOpportunityValidation.Invalid("unknown decision"),
        };
    }

    private static int Length(string? value) => value?.Trim().Length ?? 0;
}
