namespace AgentRecall.Core.Seeds;

/// <summary>
/// The registry of built-in seed packs. Packs are compiled in and immutable; nothing here
/// touches the database. Installing a pack is an explicit, opt-in action handled by
/// <see cref="ISeedPackService"/>.
/// </summary>
public static class SeedPackCatalog
{
    /// <summary>All built-in packs, ordered for stable listing.</summary>
    public static IReadOnlyList<SeedPackDefinition> All { get; } =
    [
        TidyFirstSeedPack.Definition,
    ];

    /// <summary>Finds a pack by name (case-insensitive), or null when none matches.</summary>
    public static SeedPackDefinition? Find(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}
