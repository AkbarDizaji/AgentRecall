namespace AgentRecall.Core.Domain;

/// <summary>
/// A persisted result of the host-supplied document-opportunity judge: whether a turn was a
/// good moment to offer generating a durable document (an incident report, RFC, proposal, ADR,
/// postmortem, or runbook), and the suggestion detail behind it.
///
/// This is a suggestion, not repository truth, and never causes a file to be written on its
/// own — a file is only written later, by <c>agentrecall document write</c>, once the user has
/// explicitly agreed. List-valued fields are stored as newline-separated strings, so the schema
/// stays additive and no human prose is parsed back out.
/// </summary>
public sealed class DocOpportunityCandidate
{
    public int Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Turn correlation id joining this candidate to one turn's summary; empty when none.</summary>
    public string TurnId { get; set; } = string.Empty;

    /// <summary>Stable content hash used to make detection idempotent across repeated Stop hooks.</summary>
    public string OperationHash { get; set; } = string.Empty;

    /// <summary>The kind of document the judge offered.</summary>
    public DocumentType DocumentType { get; set; }

    /// <summary>Judge confidence, 0.0-1.0.</summary>
    public double Confidence { get; set; }

    /// <summary>The judge's suggested, filename-worthy title.</summary>
    public string SuggestedTitle { get; set; } = string.Empty;

    /// <summary>Why now is a good moment for this document.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Newline-separated key points the document should cover.</summary>
    public string KeyPoints { get; set; } = string.Empty;

    /// <summary>Where the candidate came from; always the document-opportunity judge.</summary>
    public string Source { get; set; } = "HostSuppliedDocOpportunityJudge";

    public DocOpportunityStatus Status { get; set; } = DocOpportunityStatus.Open;

    /// <summary>The path <c>document write</c> wrote to, once <see cref="Status"/> is <see cref="DocOpportunityStatus.Written"/>; empty until then.</summary>
    public string WrittenPath { get; set; } = string.Empty;
}
