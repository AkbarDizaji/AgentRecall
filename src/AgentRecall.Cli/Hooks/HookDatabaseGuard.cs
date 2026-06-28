using System.Collections.Concurrent;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Hooks;

/// <summary>
/// Ensures the database is initialized at most once per process per data directory.
/// The hooks run on the synchronous prompt/turn path on every interaction, and
/// <see cref="IDatabaseInitializer.InitializeAsync"/> re-runs EnsureCreated and the
/// schema reconciler (which queries <c>sqlite_master</c>) each time — wasted work after
/// the first call. This guard collapses subsequent calls to a no-op so the hot path
/// stays cheap, while still reconciling once when a process first touches the database.
/// </summary>
internal static class HookDatabaseGuard
{
    private static readonly ConcurrentDictionary<string, bool> Initialized = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes the database the first time this process sees its path; a no-op on
    /// every later call. A failed initialization is not cached, so a transient error can
    /// be retried on the next interaction.
    /// </summary>
    public static async Task EnsureInitializedAsync(
        IServiceProvider scopedServices,
        CancellationToken cancellationToken = default)
    {
        var path = scopedServices.GetRequiredService<AgentRecallOptions>().DatabasePath;
        if (Initialized.ContainsKey(path))
        {
            return;
        }

        await scopedServices.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(cancellationToken).ConfigureAwait(false);
        Initialized[path] = true;
    }

    /// <summary>Test seam: forgets the cached initialization for a path so it runs again.</summary>
    internal static void Reset(string databasePath) => Initialized.TryRemove(databasePath, out _);
}
