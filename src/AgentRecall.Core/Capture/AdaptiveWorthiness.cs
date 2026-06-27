using AgentRecall.Core.Memory;

namespace AgentRecall.Core.Capture;

/// <summary>
/// The outcome-aware adjustment of a base capture decision: what the candidate became
/// once the evidence in a <see cref="CaptureContext"/> was weighed on top of the
/// text-only verdict. It never replaces the <see cref="ICaptureDecisionPolicy"/>; it
/// only raises or lowers its result.
/// </summary>
/// <param name="Outcome">The adjusted AutoCapture / SuggestCapture / Skip decision.</param>
/// <param name="Confidence">The adjusted confidence, 0.0–1.0 (repeats and evidence raise it).</param>
/// <param name="Reason">Why the candidate was kept (or not) — the outcome-aware evidence.</param>
/// <param name="Explanation">A human-readable account of the adjustment.</param>
public sealed record AdaptiveWorthinessResult(
    CaptureOutcome Outcome,
    double Confidence,
    CaptureReason Reason,
    string Explanation);

/// <summary>
/// The outcome-aware layer of the capture pipeline. Given the text-only worthiness
/// verdict, the base capture decision, and the context the candidate came out of, it
/// returns an adjusted decision so an observed agent failure can elevate a generic
/// lesson and a bare code fact is never auto-captured just because something broke.
/// Deterministic and rule-based: same inputs, same output. No LLM, no embeddings.
/// </summary>
public interface IAdaptiveWorthinessPolicy
{
    /// <summary>
    /// Adjusts <paramref name="baseDecision"/> using the outcome evidence in
    /// <paramref name="context"/>, the worthiness verdict, and whether a duplicate or
    /// conflict was found.
    /// </summary>
    AdaptiveWorthinessResult Adjust(
        MemoryWorthinessResult? worthiness,
        CaptureContext context,
        CaptureDecision baseDecision,
        bool isDuplicate,
        bool conflictExists);
}
