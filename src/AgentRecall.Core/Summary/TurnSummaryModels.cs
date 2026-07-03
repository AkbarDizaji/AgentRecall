namespace AgentRecall.Core.Summary;

/// <summary>A rule referenced in a turn summary, with a short title (never the full body).</summary>
public sealed record TurnSummaryRule
{
    public required int Id { get; init; }

    /// <summary>A short, single-line title — the rule's trigger or a truncated rule text.</summary>
    public required string Title { get; init; }

    /// <summary>The rule category, when known and not the default Unknown.</summary>
    public string? Category { get; init; }

    /// <summary>An optional short reason (e.g. the capture evidence), when relevant.</summary>
    public string? Reason { get; init; }

    /// <summary>True when the rule came from a built-in seed pack (shown as a [seed] marker).</summary>
    public bool Seed { get; init; }
}

/// <summary>A skipped capture candidate in a turn summary: why it was not stored.</summary>
public sealed record TurnSummarySkip
{
    /// <summary>An optional short label for the candidate; usually null (only a reason is kept).</summary>
    public string? Title { get; init; }

    public required string Reason { get; init; }
}

/// <summary>
/// The aggregated, per-turn view of what AgentRecall did: the rules it used, captured,
/// suggested, and skipped, plus interactive remember/ignore decisions and any recoverable
/// errors. Built from structured activity and finalization records — never by parsing
/// rendered notices — and deliberately holds short titles only, never full rule bodies.
/// </summary>
public sealed record TurnSummary
{
    /// <summary>The turn correlation id, or null when the summary fell back to a time window.</summary>
    public string? TurnId { get; init; }

    public IReadOnlyList<TurnSummaryRule> Used { get; init; } = [];
    public IReadOnlyList<TurnSummaryRule> Captured { get; init; } = [];
    public IReadOnlyList<TurnSummaryRule> Suggested { get; init; } = [];
    public IReadOnlyList<TurnSummarySkip> Skipped { get; init; } = [];
    public IReadOnlyList<TurnSummaryRule> Remembered { get; init; } = [];
    public IReadOnlyList<TurnSummaryRule> Ignored { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>True when nothing of note happened on the turn.</summary>
    public bool IsEmpty =>
        Used.Count == 0 && Captured.Count == 0 && Suggested.Count == 0 &&
        Skipped.Count == 0 && Remembered.Count == 0 && Ignored.Count == 0 && Errors.Count == 0;
}
