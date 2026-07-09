using AgentRecall.Core.Capture.Judge;

namespace AgentRecall.Core.Abstractions;

/// <summary>
/// The seam through which AgentRecall obtains the semantic judge's verdict for a completed
/// turn. The judge — the model, not AgentRecall — decides whether the turn holds memory-worthy
/// content; the system only validates the returned <see cref="CaptureJudgeVerdict"/> and
/// persists it safely.
///
/// A <c>null</c> return means the judge is unavailable for this turn (no verdict was supplied,
/// or no provider is configured). The turn finalizer treats that as "skip, no automatic
/// capture" — it never falls back to keyword-driven capture.
/// </summary>
public interface ICaptureJudge
{
    /// <summary>Returns the judge's verdict for the turn, or <c>null</c> when unavailable.</summary>
    Task<CaptureJudgeVerdict?> JudgeAsync(CaptureJudgeInput input, CancellationToken cancellationToken = default);
}
