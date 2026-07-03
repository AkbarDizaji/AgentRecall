using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Seeds;

/// <summary>
/// One curated rule inside a <see cref="SeedPackDefinition"/>. This is the source-of-truth
/// shape a pack author writes; the installer turns it into a <see cref="RecallRule"/>.
///
/// Seed rules are always conditional and practical, following the AgentRecall rule shape:
/// a <see cref="Trigger"/> (When/If condition), an <see cref="Action"/> (Do), an
/// <see cref="AntiPattern"/> (Avoid), a <see cref="Because"/> (reason), and an optional
/// <see cref="Exception"/>. Vague slogans ("write clean code") are deliberately not the
/// shape this record can express well.
/// </summary>
public sealed record SeedRuleDefinition
{
    /// <summary>
    /// A stable, human-readable key unique within the pack (e.g. "guard-clauses"). It is
    /// independent of wording and database id, so it survives edits and makes reinstalls
    /// idempotent. Never change a shipped key — it is how an installed rule is recognised.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>A short title (the trigger cue), e.g. "Flatten nested conditionals with guard clauses".</summary>
    public required string Title { get; init; }

    /// <summary>The "When/If condition" that should bring this rule to mind.</summary>
    public required string Trigger { get; init; }

    /// <summary>The actionable "Do" guidance.</summary>
    public required string Action { get; init; }

    /// <summary>The "Avoid" anti-pattern this rule exists to prevent.</summary>
    public required string AntiPattern { get; init; }

    /// <summary>The "Because" reason the rule holds.</summary>
    public required string Because { get; init; }

    /// <summary>An optional "Exception when useful" caveat; empty when there is none.</summary>
    public string Exception { get; init; } = string.Empty;

    /// <summary>Comma-separated tags. The pack's own tags (seed, pack name) are added automatically.</summary>
    public required string Tags { get; init; }

    /// <summary>
    /// What kind of knowledge this seed captures. Tidy/refactoring guidance is an
    /// <see cref="RuleCategory.EngineeringLesson"/>: a reusable why/pattern, not a fact
    /// about this repository.
    /// </summary>
    public RuleCategory Category { get; init; } = RuleCategory.EngineeringLesson;
}
