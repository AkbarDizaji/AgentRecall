using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Outcomes;

/// <summary>A request to record an outcome against one rule or a whole retrieval.</summary>
public sealed record OutcomeRequest
{
    /// <summary>Target a single rule. Takes precedence over <see cref="RetrievalId"/>.</summary>
    public int? RuleId { get; init; }

    /// <summary>Target every rule injected for a retrieval.</summary>
    public string? RetrievalId { get; init; }

    /// <summary>Optional task/interaction identifier recorded with the outcome.</summary>
    public string? TaskId { get; init; }

    public required OutcomeType Type { get; init; }

    /// <summary>Why the outcome is being recorded; a default is used when omitted.</summary>
    public string? Reason { get; init; }

    /// <summary>Allow a second identical outcome to adjust confidence again.</summary>
    public bool AllowDuplicate { get; init; }
}

/// <summary>A single confidence adjustment applied to one rule.</summary>
public sealed record RuleAdjustment
{
    public required int RuleId { get; init; }
    public required OutcomeType Type { get; init; }
    public required double PreviousConfidence { get; init; }
    public required double NewConfidence { get; init; }

    /// <summary>The confidence change actually applied after clamping.</summary>
    public required double Delta { get; init; }

    public required string Reason { get; init; }
}

/// <summary>The result of recording an outcome.</summary>
public sealed record OutcomeResult
{
    /// <summary>False when outcome tracking is disabled by configuration.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The adjustments applied (one per affected rule).</summary>
    public IReadOnlyList<RuleAdjustment> Adjustments { get; init; } = [];

    /// <summary>How many records were skipped as duplicates.</summary>
    public int SkippedDuplicates { get; init; }

    /// <summary>A problem that prevented recording (e.g. unknown retrieval), if any.</summary>
    public string? Error { get; init; }
}
