namespace AgentRecall.Core.Seeds;

/// <summary>
/// A curated, opt-in pack of starter engineering rules (see <see cref="SeedRuleDefinition"/>).
/// Packs are built-in and immutable; installing one materialises its rules as
/// seed-derived <see cref="Domain.RecallRule"/> rows the user can then curate like any other.
/// </summary>
public sealed record SeedPackDefinition
{
    /// <summary>The pack's stable identifier used on the command line (e.g. "tidy-first").</summary>
    public required string Name { get; init; }

    /// <summary>A one-line description of what the pack offers.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// A short provenance/copyright note surfaced by <c>seed show</c>. Seed packs paraphrase
    /// common practice into original conditional rules; they must not copy source text.
    /// </summary>
    public required string CopyrightNote { get; init; }

    /// <summary>The rules this pack installs.</summary>
    public required IReadOnlyList<SeedRuleDefinition> Rules { get; init; }
}
