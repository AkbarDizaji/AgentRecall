using AgentRecall.Core.Capture;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Memory;

namespace AgentRecall.Core.Abstractions;

/// <summary>
/// The persisted artifacts produced from a single piece of feedback. Both
/// <see cref="Event"/> and <see cref="Rule"/> are null when the memory-worthiness
/// policy rejected the candidate and nothing was stored.
/// </summary>
public sealed record FeedbackResult(RecallEvent? Event, RecallRule? Rule)
{
    /// <summary>
    /// True when capture found an equivalent existing rule and recorded the
    /// feedback against it instead of creating a duplicate.
    /// </summary>
    public bool ReusedExistingRule { get; init; }

    /// <summary>
    /// The memory-worthiness verdict applied to the candidate, when screening was
    /// enabled; <c>null</c> when screening was disabled.
    /// </summary>
    public MemoryWorthinessResult? Worthiness { get; init; }

    /// <summary>
    /// The deterministic capture decision (AutoCapture / SuggestCapture / Skip) that
    /// AgentRecall reached for this candidate, with the reason, confidence, scope and
    /// notice used to inform the user. <c>null</c> only on the legacy path before a
    /// decision was computed.
    /// </summary>
    public CaptureDecision? Decision { get; init; }

    /// <summary>
    /// The outcome-aware reason the candidate was captured (or reinforced), when an
    /// adaptive <see cref="Feedback.FeedbackInput.Context"/> was supplied. Defaults to
    /// <see cref="CaptureReason.None"/>.
    /// </summary>
    public CaptureReason CaptureReason { get; init; } = CaptureReason.None;

    /// <summary>A short, human-readable account of the evidence behind the capture, if any.</summary>
    public string? EvidenceSummary { get; init; }

    /// <summary>True when a rule was actually stored (not rejected as a code fact).</summary>
    public bool RuleStored => Rule is not null;
}

/// <summary>
/// Captures feedback: stores it as a <see cref="RecallEvent"/> and extracts a
/// pending <see cref="RecallRule"/> from it.
/// </summary>
public interface IFeedbackService
{
    Task<FeedbackResult> AddAsync(FeedbackInput input, CancellationToken cancellationToken = default);
}
