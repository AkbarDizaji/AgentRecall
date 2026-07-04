using AgentRecall.Core.Domain;

namespace AgentRecall.Core.CareerImpact;

/// <summary>The turn text the deterministic detector inspects. No transcript is stored.</summary>
public sealed record CareerImpactInput
{
    public string? Prompt { get; init; }
    public string? Response { get; init; }

    /// <summary>Rule texts captured on the turn, folded into the signal haystack.</summary>
    public IReadOnlyList<string> CapturedRuleTexts { get; init; } = [];
}

/// <summary>A request to analyze one finalized turn for career impact.</summary>
public sealed record CareerImpactTurnRequest
{
    public string? Prompt { get; init; }
    public string? Response { get; init; }
    public IReadOnlyList<string> CapturedRuleTexts { get; init; } = [];
    public string? TurnId { get; init; }
    public string Source { get; init; } = "cli";
}

/// <summary>The ADR recommendation portion of a <see cref="CareerImpactAnalysis"/>.</summary>
public sealed record CareerImpactAdr
{
    public bool Recommended { get; init; }
    public string SuggestedTitle { get; init; } = string.Empty;
    public string Context { get; init; } = string.Empty;
    public string Decision { get; init; } = string.Empty;
    public IReadOnlyList<string> Alternatives { get; init; } = [];
    public IReadOnlyList<string> Consequences { get; init; } = [];
}

/// <summary>
/// The deterministic output of <see cref="CareerImpactDetector"/>: whether a turn involved
/// significant, promotion-worthy engineering work and the structured coaching detail derived
/// from it. Pure data — no LLM, embeddings, or external services produced it.
/// </summary>
public sealed record CareerImpactAnalysis
{
    public bool IsSignificant { get; init; }

    /// <summary>True when there is any positive signal at all (used by <c>Always</c> mode).</summary>
    public bool HasSignal { get; init; }

    public double Confidence { get; init; }
    public int PromotionWorthiness { get; init; }
    public IReadOnlyList<ImpactCategory> Categories { get; init; } = [];
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public IReadOnlyList<string> SuggestedMetrics { get; init; } = [];
    public IReadOnlyList<string> SuggestedEvidence { get; init; } = [];
    public IReadOnlyList<string> Stakeholders { get; init; } = [];
    public IReadOnlyList<string> NextActions { get; init; } = [];
    public CareerImpactAdr Adr { get; init; } = new();
    public string WhyThisMatters { get; init; } = string.Empty;
    public string TechnicalImpact { get; init; } = string.Empty;
    public string BusinessImpact { get; init; } = string.Empty;
    public string LongTermImpact { get; init; } = string.Empty;
    public string PromotionNote { get; init; } = string.Empty;
    public string CompactSummary { get; init; } = string.Empty;
}
