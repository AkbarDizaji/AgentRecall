using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;

namespace AgentRecall.Core.Abstractions;

/// <summary>
/// Converts raw <see cref="FeedbackInput"/> into a candidate <see cref="RecallRule"/>.
/// Ships with a rule-based implementation; an LLM-backed one may follow.
/// </summary>
public interface IRecallExtractor
{
    /// <summary>
    /// Produces a candidate rule from feedback. The returned rule is not yet
    /// persisted and should carry <see cref="RuleStatus.Pending"/>.
    /// </summary>
    RecallRule Extract(FeedbackInput input);
}
