using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Mining;

/// <summary>Tuning for a mining run.</summary>
public sealed record MiningOptions
{
    /// <summary>Minimum repeats of a normalized signal before it is proposed.</summary>
    public int MinOccurrences { get; init; } = 3;
}

/// <summary>The outcome of a mining run.</summary>
public sealed record MiningResult
{
    /// <summary>All currently-suggested candidates (newly created or updated), best first.</summary>
    public required IReadOnlyList<LessonCandidate> Suggested { get; init; }

    /// <summary>Candidates created this run.</summary>
    public required int Created { get; init; }

    /// <summary>Existing suggested candidates refreshed this run.</summary>
    public required int Updated { get; init; }

    /// <summary>Clusters skipped because an Active/Promoted rule already covers them.</summary>
    public required int SuppressedByRule { get; init; }

    /// <summary>Clusters skipped because the same pattern was previously rejected.</summary>
    public required int SuppressedByRejection { get; init; }
}
