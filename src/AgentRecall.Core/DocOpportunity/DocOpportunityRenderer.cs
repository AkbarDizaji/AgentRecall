using System.Text;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.DocOpportunity;

/// <summary>
/// Renders a <see cref="DocOpportunityCandidate"/> for humans. Output is deliberately bounded —
/// the turn-summary pointer is one line, and the compact/status views cap their lists — so the
/// automatic end-of-turn surface never bloats the session or leaks a raw prose dump.
/// </summary>
public static class DocOpportunityRenderer
{
    /// <summary>Badge for the automatic document-opportunity notice.</summary>
    public const string Badge = "📄 **AgentRecall Document Opportunity:**";

    /// <summary>Message shown when no candidate has been offered yet.</summary>
    public const string NoCandidateMessage =
        Badge + " no document opportunity recorded yet — run `agentrecall document status` " +
        "to check the mode is not Off.";

    /// <summary>
    /// Builds the single-line pointer surfaced inside the Turn Memory Summary. Never includes
    /// the reason or key points — those stay out of the model-visible surface until the user
    /// asks and Claude reads the full candidate.
    /// </summary>
    public static string BuildTurnSummaryPointer(DocumentType documentType, string suggestedTitle, double confidence)
    {
        var title = string.IsNullOrWhiteSpace(suggestedTitle) ? "(untitled)" : suggestedTitle.Trim();
        return $"possible {DocumentTypeNames.DisplayName(documentType)} opportunity: \"{title}\" " +
               "— ask the user before running `agentrecall document write`";
    }

    /// <summary>Compact one-block summary for the human CLI path after <c>finalize-turn</c>.</summary>
    public static string RenderCompact(DocOpportunityCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var sb = new StringBuilder();
        sb.Append(Badge).Append(" possible ")
          .Append(DocumentTypeNames.DisplayName(candidate.DocumentType))
          .Append(" opportunity: \"").Append(candidate.SuggestedTitle).Append("\".");

        if (!string.IsNullOrWhiteSpace(candidate.Reason))
        {
            sb.Append("\n- Why: ").Append(candidate.Reason);
        }

        var keyPoints = DocOpportunityMapping.KeyPoints(candidate);
        if (keyPoints.Count > 0)
        {
            sb.Append("\n- Covers: ").Append(string.Join("; ", keyPoints.Take(5)));
        }

        sb.Append("\n\nAsk the user before generating this — then run `agentrecall document write`.");
        return sb.ToString();
    }

    /// <summary>Human-facing text for the <c>document status</c> command.</summary>
    public static string RenderStatus(DocOpportunityCandidate? candidate)
    {
        if (candidate is null)
        {
            return "Last candidate:  (none offered yet)";
        }

        var sb = new StringBuilder();
        sb.Append("Last candidate:  ").Append(DocumentTypeNames.DisplayName(candidate.DocumentType))
          .Append(" — \"").Append(candidate.SuggestedTitle).Append('"')
          .Append(" (confidence ").Append(candidate.Confidence.ToString("0.00")).Append(", ").Append(candidate.Status).Append(')');

        if (candidate.Status == DocOpportunityStatus.Written && !string.IsNullOrWhiteSpace(candidate.WrittenPath))
        {
            sb.Append("\nWritten to:      ").Append(candidate.WrittenPath);
        }

        return sb.ToString();
    }
}
