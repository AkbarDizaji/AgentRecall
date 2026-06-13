namespace AgentRecall.Core.Abstractions;

/// <summary>The result of inspecting a message for reusable feedback.</summary>
public sealed record FeedbackCandidate(bool IsCandidate, string SuggestedRule);

/// <summary>
/// Decides whether a conversational message looks like a reusable correction
/// (e.g. "don't use string interpolation for SQL") and, if so, proposes a rule.
/// It never saves anything — it only surfaces candidates. Deterministic; no LLM.
/// </summary>
public interface IFeedbackCandidateAnalyzer
{
    FeedbackCandidate Analyze(string message);
}
