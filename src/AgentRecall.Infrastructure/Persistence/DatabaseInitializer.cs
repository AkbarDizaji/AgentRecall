using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentRecall.Infrastructure.Persistence;

/// <summary>
/// Creates the data directory and SQLite schema using EF Core's
/// <c>EnsureCreated</c>. Migrations are deferred to a later phase.
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

        if (created)
        {
            _logger.LogInformation("Created AgentRecall database at {Path}", _options.DatabasePath);
        }
        else
        {
            _logger.LogDebug("AgentRecall database already present at {Path}", _options.DatabasePath);
        }

        return _options.DatabasePath;
    }
}
