using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Compression;

/// <summary>How a group of rules relate to one another.</summary>
public enum RuleRelationship
{
    /// <summary>The same guidance stated the same way.</summary>
    Duplicate,

    /// <summary>The same guidance with minor wording differences.</summary>
    NearDuplicate,

    /// <summary>Different wording about the same subject (e.g. repeated corrections).</summary>
    Overlapping,
}

/// <summary>Knobs controlling which rules are considered compressible.</summary>
public sealed record CompressionOptions
{
    /// <summary>Restrict compression to a scope granularity, if set.</summary>
    public ScopeLevel? ScopeLevel { get; init; }

    /// <summary>Restrict compression to a scope identifier, if set.</summary>
    public string? ScopeValue { get; init; }

    /// <summary>Token-overlap (Jaccard) at or above which two rules overlap.</summary>
    public double OverlapThreshold { get; init; } = 0.34;

    /// <summary>Token-overlap at or above which two rules are near-duplicates.</summary>
    public double NearDuplicateThreshold { get; init; } = 0.85;

    /// <summary>
    /// When true, rules that share a recurring subject token (e.g. "sql") are
    /// grouped even when their overall wording overlap is low.
    /// </summary>
    public bool UseSharedAnchors { get; init; } = true;

    public static CompressionOptions Default { get; } = new();
}

/// <summary>
/// A detected group of compressible rules and the canonical rule they would
/// collapse into. Produced by analysis without mutating anything.
/// </summary>
public sealed record CompressionCandidate
{
    public required string CanonicalRuleText { get; init; }
    public required IReadOnlyList<RecallRule> Sources { get; init; }
    public required RuleRelationship Relationship { get; init; }

    /// <summary>The shared subject the group is about (e.g. "sql").</summary>
    public required string Subject { get; init; }
}

/// <summary>Aggregate statistics for a compression run (actual or projected).</summary>
public sealed record CompressionStats
{
    /// <summary>Number of groups that can be / were compressed.</summary>
    public required int CandidateCompressions { get; init; }

    /// <summary>Total source rules folded into canonical rules.</summary>
    public required int RulesMerged { get; init; }

    /// <summary>Number of canonical rules produced.</summary>
    public required int CanonicalRulesCreated { get; init; }

    /// <summary>Active rule count before compression.</summary>
    public required int RulesBefore { get; init; }

    /// <summary>Active rule count after compression.</summary>
    public required int RulesAfter { get; init; }

    /// <summary>Percentage reduction in the active rule count, 0–100.</summary>
    public required double MemoryReductionPercentage { get; init; }
}

/// <summary>The result of analysing memory for compression without applying it.</summary>
public sealed record CompressionAnalysis
{
    public required IReadOnlyList<CompressionCandidate> Candidates { get; init; }

    /// <summary>Statistics that would result if every candidate were compressed.</summary>
    public required CompressionStats Stats { get; init; }
}

/// <summary>One applied compression: the canonical rule and the rules it replaced.</summary>
public sealed record CompressedGroup
{
    public required RecallRule Canonical { get; init; }
    public required IReadOnlyList<RecallRule> Sources { get; init; }
    public required RuleRelationship Relationship { get; init; }
    public required string Subject { get; init; }

    /// <summary>The audit event recording this compression.</summary>
    public required int AuditEventId { get; init; }
}

/// <summary>The result of applying compression.</summary>
public sealed record CompressionResult
{
    public required IReadOnlyList<CompressedGroup> Groups { get; init; }
    public required CompressionStats Stats { get; init; }
}
