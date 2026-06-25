namespace AgentRecall.Core.Domain;

/// <summary>
/// A record of one turn finalization: what AgentRecall extracted from a completed
/// turn and decided to capture, suggest, or skip. Persisted so the agent can query
/// the last finalization result instead of guessing whether the Stop hook captured
/// anything.
///
/// Privacy by default: the raw turn transcript is not stored unless
/// <see cref="Configuration.AgentRecallOptions.StoreTurnTranscript"/> is enabled.
/// Otherwise only a content hash, the resulting rule ids, and skip reasons are kept.
/// </summary>
public sealed class TurnFinalization
{
    public int Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The working directory the turn ran in (used to scope the lookup).</summary>
    public string Cwd { get; set; } = string.Empty;

    /// <summary>Where the finalization was triggered from (e.g. <c>stop_hook</c>, <c>manual</c>).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Comma-separated ids of rules captured (Active) on this turn.</summary>
    public string CapturedRuleIds { get; set; } = string.Empty;

    /// <summary>Comma-separated ids of rules parked as Pending suggestions on this turn.</summary>
    public string SuggestedRuleIds { get; set; } = string.Empty;

    /// <summary>Newline-separated reasons candidates were skipped.</summary>
    public string SkippedReasons { get; set; } = string.Empty;

    /// <summary>Comma-separated ids of existing rules a candidate duplicated.</summary>
    public string DuplicateRuleIds { get; set; } = string.Empty;

    /// <summary>A short summary of any non-fatal errors encountered while finalizing.</summary>
    public string ErrorSummary { get; set; } = string.Empty;

    /// <summary>Stable hash of the turn content, used to make finalization idempotent.</summary>
    public string RawHash { get; set; } = string.Empty;

    /// <summary>
    /// The raw turn transcript, stored only when transcript persistence is enabled;
    /// otherwise empty.
    /// </summary>
    public string Transcript { get; set; } = string.Empty;
}
