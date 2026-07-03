namespace AgentRecall.Core.Seeds;

/// <summary>
/// Installs, removes, and reports on built-in seed packs. Seed packs are opt-in curated
/// starter rules; nothing here runs unless the user explicitly asks for it.
/// </summary>
public interface ISeedPackService
{
    /// <summary>Lists the built-in packs with their installed state.</summary>
    Task<IReadOnlyList<SeedPackListing>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Full detail for one pack (rules, defaults, provenance), or null when unknown.</summary>
    Task<SeedPackDetail?> ShowAsync(string packName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs a pack idempotently: creates missing rules, leaves existing ones untouched,
    /// and (only with <see cref="SeedInstallOptions.Force"/>) restores previously removed
    /// rules that the user has not modified. Throws <see cref="KeyNotFoundException"/> for an
    /// unknown pack.
    /// </summary>
    Task<SeedInstallResult> InstallAsync(string packName, SeedInstallOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a pack by archiving its seed rules. User-modified or promoted rules are
    /// preserved, not deleted. Throws <see cref="KeyNotFoundException"/> for an unknown pack.
    /// </summary>
    Task<SeedRemoveResult> RemoveAsync(string packName, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>Installed-status counts for every built-in pack.</summary>
    Task<IReadOnlyList<SeedPackStatus>> StatusAsync(CancellationToken cancellationToken = default);
}
