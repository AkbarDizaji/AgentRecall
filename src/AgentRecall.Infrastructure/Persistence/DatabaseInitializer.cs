using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentRecall.Infrastructure.Persistence;

/// <summary>
/// Creates the data directory and SQLite schema using EF Core's
/// <c>EnsureCreated</c>, then runs <see cref="SchemaReconciler"/> to bring any
/// pre-existing database created by an earlier version up to the current model.
/// Migrations are not used yet.
/// </summary>
public sealed class DatabaseInitializer : IDatabaseInitializer
{
    private readonly AgentRecallDbContext _db;
    private readonly AgentRecallOptions _options;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        AgentRecallDbContext db,
        AgentRecallOptions options,
        ILogger<DatabaseInitializer> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.DataDirectory);

        var created = await _db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        // Write-Ahead Logging lets readers proceed while a writer holds the database, which
        // matters because AgentRecall runs as several concurrent short-lived processes (the MCP
        // server plus one per hook fire) against one file. The setting is persisted on the file,
        // so re-running it is a cheap no-op. Best-effort: a transient lock here must not fail
        // initialization — the command timeout still serialises writers without WAL.
        try
        {
            await _db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not enable WAL journal mode; continuing with the default journal.");
        }

        if (created)
        {
            _logger.LogInformation("Created AgentRecall database at {Path}", _options.DatabasePath);
        }
        else
        {
            _logger.LogDebug("AgentRecall database already present at {Path}", _options.DatabasePath);
        }

        // EnsureCreated never touches an existing database, so a file created by
        // an earlier version can be missing columns or indexes the current model
        // expects. Reconcile additively to close that gap for every user.
        await SchemaReconciler.ReconcileAsync(_db, _logger, cancellationToken).ConfigureAwait(false);

        return _options.DatabasePath;
    }
}
