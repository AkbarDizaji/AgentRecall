using AgentRecall.Core.Domain;

namespace AgentRecall.Core.DocOpportunity;

/// <summary>
/// The folder and display names for each <see cref="DocumentType"/>. Shared by the renderer
/// (for the turn-summary pointer and status text) and the CLI's <c>document write</c> command
/// (for the actual output path), so the mapping lives in exactly one place.
/// </summary>
public static class DocumentTypeNames
{
    /// <summary>The lowercase, pluralized subfolder name under the document root.</summary>
    public static string FolderName(DocumentType type) => type switch
    {
        DocumentType.Incident => "incidents",
        DocumentType.Rfc => "rfcs",
        DocumentType.Proposal => "proposals",
        DocumentType.Adr => "adrs",
        DocumentType.Postmortem => "postmortems",
        DocumentType.Runbook => "runbooks",
        _ => type.ToString().ToLowerInvariant(),
    };

    /// <summary>A short, human-facing name for the document type.</summary>
    public static string DisplayName(DocumentType type) => type switch
    {
        DocumentType.Incident => "incident report",
        DocumentType.Rfc => "RFC",
        DocumentType.Proposal => "design proposal",
        DocumentType.Adr => "ADR",
        DocumentType.Postmortem => "postmortem",
        DocumentType.Runbook => "runbook",
        _ => type.ToString(),
    };
}
