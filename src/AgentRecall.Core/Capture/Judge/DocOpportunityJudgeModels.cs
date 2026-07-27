using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Capture.Judge;

/// <summary>
/// What the host-supplied document-opportunity judge concluded about a turn. The judge — not
/// AgentRecall — owns this decision; the system only validates the structured verdict and
/// persists it. See <see cref="DocOpportunityVerdict"/>.
/// </summary>
public enum DocOpportunityDecision
{
    /// <summary>Offer to generate a document; the turn is a good moment for one.</summary>
    Offer,

    /// <summary>Nothing worth offering this turn.</summary>
    Skip,
}

/// <summary>
/// The document-opportunity judge's structured verdict for a turn — deserialized from the
/// strict JSON the host model produces. AgentRecall validates this (see
/// <see cref="DocOpportunityValidator"/>) and persists it as a candidate; it is never trusted
/// blindly, and it never causes a file to be written on its own — only a short pointer in the
/// turn summary. Generating the file happens later, only after the user explicitly agrees in
/// chat and the host runs <c>agentrecall document write</c> itself.
/// </summary>
public sealed record DocOpportunityVerdict
{
    /// <summary>Whether to offer generating a document this turn.</summary>
    public DocOpportunityDecision Decision { get; init; }

    /// <summary>The kind of document to offer, when the decision is <see cref="DocOpportunityDecision.Offer"/>.</summary>
    public DocumentType DocumentType { get; init; }

    /// <summary>The judge's confidence in the decision, in [0, 1].</summary>
    public double Confidence { get; init; }

    /// <summary>A short, filename-worthy title for the document, required when offering.</summary>
    public string? SuggestedTitle { get; init; }

    /// <summary>Why now is a good moment for this document.</summary>
    public string? Reason { get; init; }

    /// <summary>Bounded bullets the judge captured about what the document should cover.</summary>
    public IReadOnlyList<string> KeyPoints { get; init; } = [];

    /// <summary>Why nothing was offered, required when the decision is <see cref="DocOpportunityDecision.Skip"/>.</summary>
    public string? WhyNotOffered { get; init; }
}

/// <summary>
/// The bounded, structured payload AgentRecall hands the document-opportunity judge. Carries
/// only what the model needs to decide — never huge logs, full files, or unbounded transcript.
/// <see cref="SuppliedVerdict"/> lets the default host-supplied judge return the verdict the
/// host already produced; a future live provider would ignore it and compute its own.
/// </summary>
public sealed record DocOpportunityJudgeInput
{
    /// <summary>The latest user message in the turn (bounded).</summary>
    public string? UserPrompt { get; init; }

    /// <summary>A bounded summary of the assistant's response.</summary>
    public string? AssistantSummary { get; init; }

    /// <summary>Where the turn came from (e.g. <c>stop_hook</c>).</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The scope granularity the turn belongs to.</summary>
    public ScopeLevel ScopeLevel { get; init; }

    /// <summary>The scope identifier (e.g. repository name).</summary>
    public string? ScopeValue { get; init; }

    /// <summary>
    /// The verdict the host model already produced for this turn, when the host supplies it on
    /// the payload. The default <c>HostSuppliedDocOpportunityJudge</c> returns exactly this; a
    /// live provider ignores it.
    /// </summary>
    public DocOpportunityVerdict? SuppliedVerdict { get; init; }
}
