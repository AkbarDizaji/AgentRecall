using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgentRecall.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core persistence for <see cref="TurnJudgmentRequest"/>. Stamps the timestamp on insert and
/// exposes the two reads the enforced-judgment path needs: the outstanding request for a chat, and
/// the request for a turn.
/// </summary>
public sealed class TurnJudgmentRequestRepository : EfRepository<TurnJudgmentRequest>, ITurnJudgmentRequestRepository
{
    public TurnJudgmentRequestRepository(AgentRecallDbContext db) : base(db)
    {
    }

    protected override void OnAdding(TurnJudgmentRequest entity)
    {
        if (entity.CreatedAt == default)
        {
            entity.CreatedAt = DateTimeOffset.UtcNow;
        }
    }

    // Ordering is by the autoincrement Id, which is monotonic with insertion time — SQLite cannot
    // ORDER BY a DateTimeOffset column, so CreatedAt is never used for ranking.
    public async Task<TurnJudgmentRequest?> FindOutstandingAsync(
        string? sessionId, string? cwd, CancellationToken cancellationToken = default)
    {
        var query = Db.Set<TurnJudgmentRequest>()
            .Where(r => r.Status == JudgmentRequestStatus.Outstanding);

        if (!string.IsNullOrEmpty(sessionId))
        {
            query = query.Where(r => r.SessionId == sessionId);
        }
        else if (!string.IsNullOrEmpty(cwd))
        {
            query = query.Where(r => r.Cwd == cwd);
        }

        return await query
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TurnJudgmentRequest?> FindByTurnAsync(string turnId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(turnId))
        {
            return null;
        }

        return await Db.Set<TurnJudgmentRequest>()
            .Where(r => r.TurnId == turnId)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
