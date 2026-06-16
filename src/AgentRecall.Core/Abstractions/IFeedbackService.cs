using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;

namespace AgentRecall.Core.Abstractions;

/// <summary>The persisted artifacts produced from a single piece of feedback.</summary>
public sealed record FeedbackResult(RecallEvent Event, RecallRule Rule)
{
    /// <summary>
    /// True when capture found an equivalent existing rule and recorded the
    /// feedback against it instead of creating a duplicate.
    /// </summary>
    public bool ReusedExistingRule { get; init; }
}

/// <summary>
/// Captures feedback: stores it as a <see cref="RecallEvent"/> and extracts a
/// pending <see cref="RecallRule"/> from it.
/// </summary>
public interface IFeedbackService
{
    Task<FeedbackResult> AddAsync(FeedbackInput input, CancellationToken cancellationToken = default);
}
