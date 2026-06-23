namespace AgentRecall.Core.Domain;

/// <summary>
/// A lesson proposed by mining historical signals (repeated corrections, PR
/// comments, failures, rejected patterns) that was not explicitly captured as a
/// rule. A candidate is reviewed by a human and only becomes a <see cref="RecallRule"/>
/// when accepted — mining never creates rules on its own.
/// </summary>
public sealed class LessonCandidate
{
    public int Id { get; set; }

    /// <summary>A short human-readable title for the proposal.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The suggested rule text, if accepted.</summary>
    public string SuggestedRule { get; set; } = string.Empty;

    /// <summary>The classified category of the suggested lesson.</summary>
    public RuleCategory Category { get; set; } = RuleCategory.Unknown;

    public LessonCandidateStatus Status { get; set; } = LessonCandidateStatus.Suggested;

    /// <summary>How many historical signals support this candidate.</summary>
    public int OccurrenceCount { get; set; }

    /// <summary>Deterministic confidence derived from the occurrence count, 0.0–1.0.</summary>
    public double Confidence { get; set; }

    /// <summary>When the earliest supporting signal occurred.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>When the latest supporting signal occurred.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Comma-separated ids of the events that support this candidate.</summary>
    public string SupportingEventIds { get; set; } = string.Empty;

    /// <summary>
    /// The deterministic normalized form of the signal, used to cluster repeats and
    /// to suppress duplicates of accepted/rejected candidates and existing rules.
    /// </summary>
    public string NormalizedKey { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Why the candidate was rejected, when <see cref="Status"/> is Rejected.</summary>
    public string? RejectedReason { get; set; }
}
