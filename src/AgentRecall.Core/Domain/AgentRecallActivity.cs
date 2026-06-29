namespace AgentRecall.Core.Domain;

/// <summary>
/// A persisted record of a user-facing AgentRecall action — what it fetched,
/// captured, skipped, resolved, mined, or recommended. This is the human-visible
/// activity ledger; it is deliberately separate from the model-visible context so
/// verbose notices never bloat injected tokens.
/// </summary>
public sealed class AgentRecallActivity
{
    public int Id { get; set; }

    /// <summary>When the activity occurred.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    public ActivityType ActivityType { get; set; }

    /// <summary>The concise, plain-text one-line summary (no emoji or Markdown).</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Optional verbose detail, stored as newline-separated plain-text lines (no
    /// emoji or Markdown). Empty when the activity has no extra detail.
    /// </summary>
    public string? Details { get; set; }

    /// <summary>Comma-separated rule ids this activity refers to, if any.</summary>
    public string? RuleIds { get; set; }

    /// <summary>Comma-separated lesson-candidate ids this activity refers to, if any.</summary>
    public string? CandidateIds { get; set; }

    /// <summary>Comma-separated lifecycle-recommendation ids this activity refers to, if any.</summary>
    public string? RecommendationIds { get; set; }

    /// <summary>Where the activity originated (e.g. "cli", "hook", "mcp", "stop_hook").</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The notice level in effect when the activity was recorded.</summary>
    public NoticeLevel NoticeLevel { get; set; }

    /// <summary>
    /// Optional stable identity of the underlying operation, used to deduplicate
    /// activity records so a repeated (cached) operation is not logged twice.
    /// </summary>
    public string? OperationHash { get; set; }

    /// <summary>
    /// Optional deterministic turn correlation id (see
    /// <see cref="Activity.TurnCorrelation"/>). Lets retrieval activity from
    /// UserPromptSubmit and capture activity from Stop/finalize-turn be joined into a
    /// single per-turn summary. Null for activity that is not tied to a specific turn.
    /// </summary>
    public string? TurnId { get; set; }
}
