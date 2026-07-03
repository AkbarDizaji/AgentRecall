using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Seeds;

/// <summary>Options controlling how a seed pack is installed.</summary>
public sealed record SeedInstallOptions
{
    /// <summary>
    /// Install rules as Suggested (Pending) instead of the default Active. Once a user
    /// opts into a pack, its rules are active from day one; this is the conservative mode
    /// for users who want to approve each rule manually first.
    /// </summary>
    public bool Suggested { get; init; }

    /// <summary>
    /// Re-add rules the user previously removed/archived. Without this, an archived seed
    /// rule stays out so a reinstall never resurrects rejected guidance. Never overwrites
    /// a user-modified rule even with force.
    /// </summary>
    public bool Force { get; init; }

    /// <summary>Provenance for the activity notice ("cli", "mcp", …).</summary>
    public string Source { get; init; } = "cli";
}

/// <summary>What happened to one seed rule during install or removal.</summary>
public enum SeedRuleOutcome
{
    /// <summary>A new rule was created from the pack.</summary>
    Added = 0,

    /// <summary>The rule already exists and was left untouched (idempotent).</summary>
    SkippedExisting = 1,

    /// <summary>The rule was previously removed/archived and was not resurrected (no --force).</summary>
    SkippedArchived = 2,

    /// <summary>The rule was edited or promoted by the user and was left untouched.</summary>
    SkippedUserModified = 3,

    /// <summary>A previously archived rule was restored (via --force).</summary>
    Restored = 4,

    /// <summary>The rule was archived (removal).</summary>
    Archived = 5,

    /// <summary>The rule was preserved during removal (user-modified or promoted).</summary>
    Preserved = 6,
}

/// <summary>One rule's fate during a seed operation.</summary>
public sealed record SeedRuleChange
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public int? RuleId { get; init; }
    public required SeedRuleOutcome Outcome { get; init; }
}

/// <summary>The result of installing a seed pack.</summary>
public sealed record SeedInstallResult
{
    public required string Pack { get; init; }

    /// <summary>The status new rules were installed as (Pending = "Suggested", or Active).</summary>
    public required RuleStatus Status { get; init; }

    /// <summary>The initial confidence assigned to newly added rules.</summary>
    public required double Confidence { get; init; }

    public required IReadOnlyList<SeedRuleChange> Changes { get; init; }

    /// <summary>Rules created or restored by this install (for notices/turn summary).</summary>
    public required IReadOnlyList<RecallRule> AffectedRules { get; init; }

    public int Added => Changes.Count(c => c.Outcome == SeedRuleOutcome.Added);
    public int Restored => Changes.Count(c => c.Outcome == SeedRuleOutcome.Restored);
    public int Skipped => Changes.Count(c =>
        c.Outcome is SeedRuleOutcome.SkippedExisting
            or SeedRuleOutcome.SkippedArchived
            or SeedRuleOutcome.SkippedUserModified);
}

/// <summary>The result of removing a seed pack.</summary>
public sealed record SeedRemoveResult
{
    public required string Pack { get; init; }
    public required IReadOnlyList<SeedRuleChange> Changes { get; init; }

    public int Archived => Changes.Count(c => c.Outcome == SeedRuleOutcome.Archived);
    public int Preserved => Changes.Count(c => c.Outcome == SeedRuleOutcome.Preserved);
}

/// <summary>A pack as shown by <c>seed list</c>.</summary>
public sealed record SeedPackListing
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required int RuleCount { get; init; }
    public required bool Installed { get; init; }
}

/// <summary>Per-pack installed status counts, for <c>seed status</c>.</summary>
public sealed record SeedPackStatus
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required int TotalRules { get; init; }
    public required bool Installed { get; init; }
    public required int Active { get; init; }
    public required int Suggested { get; init; }
    public required int Promoted { get; init; }
    public required int Archived { get; init; }

    /// <summary>Average confidence across in-force (non-archived) installed rules; 0 when none.</summary>
    public required double AverageConfidence { get; init; }
}

/// <summary>A rule title paired with its installed state, for <c>seed show</c>.</summary>
public sealed record SeedPackRuleView
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public int? RuleId { get; init; }
    public RuleStatus? Status { get; init; }
}

/// <summary>Full detail for <c>seed show &lt;pack&gt;</c>.</summary>
public sealed record SeedPackDetail
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string CopyrightNote { get; init; }
    public required int RuleCount { get; init; }
    public required RuleStatus DefaultStatus { get; init; }
    public required double DefaultConfidence { get; init; }
    public required bool Installed { get; init; }
    public required IReadOnlyList<SeedPackRuleView> Rules { get; init; }
}
