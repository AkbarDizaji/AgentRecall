namespace AgentRecall.Core.Domain;

/// <summary>
/// A persisted result of the deterministic end-of-turn career-impact detector: whether a
/// turn involved significant, promotion-worthy engineering work, and the structured coaching
/// detail (impact, evidence, metrics, stakeholders, ADR, promotion note) derived from it.
///
/// This is coaching/evidence guidance, not repository truth. List-valued fields are stored as
/// newline-separated strings and categories as a comma-separated list of
/// <see cref="ImpactCategory"/> names, so the schema stays additive and no human prose is
/// parsed back out. Persisted so <c>agentrecall career impact --last</c> and
/// <c>career journal --last</c> can answer on demand without re-running the turn.
/// </summary>
public sealed class CareerImpactCandidate
{
    public int Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Turn correlation id joining this candidate to one turn's summary; empty when none.</summary>
    public string TurnId { get; set; } = string.Empty;

    /// <summary>Stable content hash used to make detection idempotent across repeated Stop hooks.</summary>
    public string OperationHash { get; set; } = string.Empty;

    /// <summary>Whether the detector considered the work significant (promotion-worthy).</summary>
    public bool IsSignificant { get; set; }

    /// <summary>Detector confidence, 0.0–1.0.</summary>
    public double Confidence { get; set; }

    /// <summary>Promotion worthiness on a 0–10 scale.</summary>
    public int PromotionWorthiness { get; set; }

    /// <summary>Comma-separated <see cref="ImpactCategory"/> names.</summary>
    public string Categories { get; set; } = string.Empty;

    /// <summary>Newline-separated reasons the detector flagged this work.</summary>
    public string Reasons { get; set; } = string.Empty;

    public string WhyThisMatters { get; set; } = string.Empty;
    public string TechnicalImpact { get; set; } = string.Empty;
    public string BusinessImpact { get; set; } = string.Empty;
    public string LongTermImpact { get; set; } = string.Empty;

    /// <summary>Newline-separated evidence-to-collect suggestions.</summary>
    public string EvidenceToCollect { get; set; } = string.Empty;

    /// <summary>Newline-separated suggested success metrics.</summary>
    public string Metrics { get; set; } = string.Empty;

    /// <summary>Newline-separated likely stakeholders.</summary>
    public string Stakeholders { get; set; } = string.Empty;

    public bool AdrRecommended { get; set; }
    public string AdrSuggestedTitle { get; set; } = string.Empty;
    public string AdrContext { get; set; } = string.Empty;
    public string AdrDecision { get; set; } = string.Empty;

    /// <summary>Newline-separated ADR alternatives.</summary>
    public string AdrAlternatives { get; set; } = string.Empty;

    /// <summary>Newline-separated ADR consequences.</summary>
    public string AdrConsequences { get; set; } = string.Empty;

    public string PromotionNote { get; set; } = string.Empty;

    /// <summary>Newline-separated suggested next actions.</summary>
    public string NextActions { get; set; } = string.Empty;

    /// <summary>Where the candidate came from; always the deterministic detector.</summary>
    public string Source { get; set; } = "CareerImpactDetector";

    public CareerImpactStatus Status { get; set; } = CareerImpactStatus.Open;
}
