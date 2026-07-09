namespace AgentRecall.Core.Capture;

/// <summary>
/// How the Stop/finalize-turn path decides what to remember.
/// </summary>
public enum CaptureJudgeMode
{
    /// <summary>
    /// The default. A semantic judge decides whether the turn holds memory-worthy content;
    /// AgentRecall only validates the judge's verdict and persists it. When no verdict is
    /// available the turn is skipped — there is never a keyword-driven fallback.
    /// </summary>
    Semantic,

    /// <summary>Automatic Stop-hook capture is disabled entirely; the finalizer is a no-op.</summary>
    Off,
}
