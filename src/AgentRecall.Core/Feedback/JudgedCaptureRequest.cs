using AgentRecall.Core.Capture;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Feedback;

/// <summary>
/// A request to persist a rule the semantic capture judge already decided to keep. Unlike
/// <see cref="FeedbackInput"/> (which is screened by the worthiness classifier and the capture
/// decision policy), a judged request bypasses those keyword-driven layers entirely: the judge
/// is authoritative, so the system only deduplicates and persists. The <see cref="Rule"/> is
/// already built from the judge's normalized rule with its status, confidence, and category set.
/// </summary>
public sealed record JudgedCaptureRequest
{
    /// <summary>The rule to store, pre-built from the judge's normalized rule.</summary>
    public required RecallRule Rule { get; init; }

    /// <summary>The status to store a new rule under (Active for a capture, Pending for a suggestion).</summary>
    public required RuleStatus Status { get; init; }

    /// <summary>The nearest domain capture reason for the stored rule.</summary>
    public CaptureReason DomainReason { get; init; } = CaptureReason.None;

    /// <summary>A short account of the evidence behind the capture.</summary>
    public string? EvidenceSummary { get; init; }

    /// <summary>The task context recorded on the reinforcement/capture event.</summary>
    public string? TaskContext { get; init; }
}
