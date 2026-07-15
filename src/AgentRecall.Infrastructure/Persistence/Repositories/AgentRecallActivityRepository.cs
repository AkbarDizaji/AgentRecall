using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgentRecall.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core persistence for <see cref="AgentRecallActivity"/>. Stamps the timestamp on
/// insert and exposes the newest-first reads the activity log surfaces use.
/// </summary>
public sealed class AgentRecallActivityRepository : EfRepository<AgentRecallActivity>, IAgentRecallActivityRepository
{
    public AgentRecallActivityRepository(AgentRecallDbContext db) : base(db)
    {
    }

    protected override void OnAdding(AgentRecallActivity entity)
    {
        if (entity.CreatedAt == default)
        {
            entity.CreatedAt = DateTimeOffset.UtcNow;
        }
    }

    // The activity log is append-only, so the autoincrement Id is monotonic with
    // insertion time. Ordering by Id (rather than the stored DateTimeOffset, which
    // SQLite cannot ORDER BY) gives newest-first results efficiently in SQL.
    public async Task<AgentRecallActivity?> GetLatestAsync(CancellationToken cancellationToken = default) =>
        await Db.Set<AgentRecallActivity>()
            .AsNoTracking()
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<AgentRecallActivity>> ListRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        return await Db.Set<AgentRecallActivity>()
            .AsNoTracking()
            .OrderByDescending(a => a.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AgentRecallActivity>> ListByTurnAsync(string turnId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(turnId))
        {
            return [];
        }

        return await Db.Set<AgentRecallActivity>()
            .AsNoTracking()
            .Where(a => a.TurnId == turnId)
            .OrderByDescending(a => a.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentRecallActivity?> FindByOperationHashAsync(string operationHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(operationHash))
        {
            return null;
        }

        return await Db.Set<AgentRecallActivity>()
            .AsNoTracking()
            .Where(a => a.OperationHash == operationHash)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
