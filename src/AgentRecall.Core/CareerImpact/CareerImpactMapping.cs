using AgentRecall.Core.Domain;

namespace AgentRecall.Core.CareerImpact;

/// <summary>
/// Maps between the pure <see cref="CareerImpactAnalysis"/> and the persisted
/// <see cref="CareerImpactCandidate"/>. List-valued fields round-trip as newline-separated
/// strings and categories as comma-separated <see cref="ImpactCategory"/> names, so nothing
/// is parsed back out of human prose.
/// </summary>
public static class CareerImpactMapping
{
    private const char ListSeparator = '\n';

    public static CareerImpactCandidate ToEntity(CareerImpactAnalysis analysis, string? turnId, string operationHash, string source)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return new CareerImpactCandidate
        {
            TurnId = turnId ?? string.Empty,
            OperationHash = operationHash,
            IsSignificant = analysis.IsSignificant,
            Confidence = analysis.Confidence,
            PromotionWorthiness = analysis.PromotionWorthiness,
            Categories = string.Join(",", analysis.Categories.Select(c => c.ToString())),
            Reasons = JoinList(analysis.Reasons),
            WhyThisMatters = analysis.WhyThisMatters,
            TechnicalImpact = analysis.TechnicalImpact,
            BusinessImpact = analysis.BusinessImpact,
            LongTermImpact = analysis.LongTermImpact,
            EvidenceToCollect = JoinList(analysis.SuggestedEvidence),
            Metrics = JoinList(analysis.SuggestedMetrics),
            Stakeholders = JoinList(analysis.Stakeholders),
            AdrRecommended = analysis.Adr.Recommended,
            AdrSuggestedTitle = analysis.Adr.SuggestedTitle,
            AdrContext = analysis.Adr.Context,
            AdrDecision = analysis.Adr.Decision,
            AdrAlternatives = JoinList(analysis.Adr.Alternatives),
            AdrConsequences = JoinList(analysis.Adr.Consequences),
            PromotionNote = analysis.PromotionNote,
            NextActions = JoinList(analysis.NextActions),
            Source = string.IsNullOrWhiteSpace(source) ? "CareerImpactDetector" : source,
            Status = CareerImpactStatus.Open,
        };
    }

    public static CareerImpactAnalysis ToAnalysis(CareerImpactCandidate entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var significant = entity.IsSignificant;
        return new CareerImpactAnalysis
        {
            IsSignificant = significant,
            HasSignal = true,
            Confidence = entity.Confidence,
            PromotionWorthiness = entity.PromotionWorthiness,
            Categories = ParseCategories(entity.Categories),
            Reasons = SplitList(entity.Reasons),
            SuggestedMetrics = SplitList(entity.Metrics),
            SuggestedEvidence = SplitList(entity.EvidenceToCollect),
            Stakeholders = SplitList(entity.Stakeholders),
            NextActions = SplitList(entity.NextActions),
            Adr = new CareerImpactAdr
            {
                Recommended = entity.AdrRecommended,
                SuggestedTitle = entity.AdrSuggestedTitle,
                Context = entity.AdrContext,
                Decision = entity.AdrDecision,
                Alternatives = SplitList(entity.AdrAlternatives),
                Consequences = SplitList(entity.AdrConsequences),
            },
            WhyThisMatters = entity.WhyThisMatters,
            TechnicalImpact = entity.TechnicalImpact,
            BusinessImpact = entity.BusinessImpact,
            LongTermImpact = entity.LongTermImpact,
            PromotionNote = entity.PromotionNote,
            CompactSummary = significant ? "possible Staff-level impact detected" : "possible engineering impact detected",
        };
    }

    private static string JoinList(IReadOnlyList<string> items) => string.Join(ListSeparator, items);

    private static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrEmpty(value)
            ? []
            : value.Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<ImpactCategory> ParseCategories(string? value) =>
        string.IsNullOrEmpty(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Enum.TryParse<ImpactCategory>(s, out var c) ? c : (ImpactCategory?)null)
                .Where(c => c is not null)
                .Select(c => c!.Value)
                .ToList();
}
