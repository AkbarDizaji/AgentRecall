using AgentRecall.Core.Capture;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Memory;

/// <summary>
/// What the classifier decided to do with a candidate. A readable projection of
/// <see cref="MemoryWorthiness"/>: WorthStoring → Store, NotWorthStoring → Reject,
/// NeedsReview → NeedsReview.
/// </summary>
public enum MemoryDecision
{
    Store,
    Reject,
    NeedsReview,
}

/// <summary>
/// Verdict for whether a candidate memory is worth storing as a
/// <see cref="Domain.RecallRule"/>.
/// </summary>
public enum MemoryWorthiness
{
    /// <summary>A reusable engineering lesson; store it normally.</summary>
    WorthStoring,

    /// <summary>
    /// A low-value code fact (recoverable from the repository with search); do not
    /// store a rule by default.
    /// </summary>
    NotWorthStoring,

    /// <summary>
    /// Too specific to store as-is, but it hints at a reusable pattern. Store the
    /// generalized lesson instead of the raw fact.
    /// </summary>
    NeedsReview,
}

/// <summary>
/// The outcome of classifying a candidate memory for worthiness.
/// </summary>
/// <param name="Verdict">Whether and how the candidate should be stored.</param>
/// <param name="Reason">Why the classifier reached this verdict.</param>
/// <param name="Confidence">Confidence in the verdict, 0.0–1.0.</param>
/// <param name="SuggestedGeneralizedLesson">
/// For <see cref="MemoryWorthiness.NeedsReview"/>, the reusable lesson to store in
/// place of the raw code fact; otherwise <c>null</c>.
/// </param>
/// <param name="Category">Which kind of knowledge the candidate is.</param>
/// <param name="CaptureReason">
/// The outcome-aware reason the classifier already knows from the text alone — today
/// only <see cref="Capture.CaptureReason.ExplicitUserPreference"/> for an explicitly
/// stated preference. <see cref="Capture.CaptureReason.None"/> otherwise, so callers
/// that supply their own context are unaffected.
/// </param>
/// <param name="NormalizedTrigger">
/// A durable "when …" condition to store as the rule's trigger, when the classifier
/// rewrote the candidate (e.g. a normalized user preference); otherwise <c>null</c>.
/// </param>
/// <param name="EvidenceSummary">A short account of the evidence, when the classifier knows it.</param>
/// <param name="Tags">Comma-separated tags to attach, when the classifier assigns any.</param>
public sealed record MemoryWorthinessResult(
    MemoryWorthiness Verdict,
    string Reason,
    double Confidence,
    string? SuggestedGeneralizedLesson = null,
    RuleCategory Category = RuleCategory.Unknown,
    CaptureReason CaptureReason = CaptureReason.None,
    string? NormalizedTrigger = null,
    string? EvidenceSummary = null,
    string? Tags = null)
{
    /// <summary>True when the candidate is an explicitly stated user preference.</summary>
    public bool IsExplicitUserPreference => CaptureReason == CaptureReason.ExplicitUserPreference;

    /// <summary>The store/reject/review decision, projected from <see cref="Verdict"/>.</summary>
    public MemoryDecision Decision => Verdict switch
    {
        MemoryWorthiness.WorthStoring => MemoryDecision.Store,
        MemoryWorthiness.NeedsReview => MemoryDecision.NeedsReview,
        _ => MemoryDecision.Reject,
    };
}

/// <summary>
/// Decides whether a candidate memory is a reusable engineering lesson worth
/// storing, or a low-value code fact that can be rediscovered from the repository.
/// Deterministic and rule-based — no LLM, no embeddings.
/// </summary>
public interface IMemoryWorthinessClassifier
{
    /// <summary>Classifies the candidate guidance text for memory worthiness.</summary>
    MemoryWorthinessResult Classify(string candidate);
}
