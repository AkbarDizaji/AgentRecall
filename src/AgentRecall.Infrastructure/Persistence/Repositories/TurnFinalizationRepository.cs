using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Finalization;
using Microsoft.EntityFrameworkCore;

namespace AgentRecall.Infrastructure.Persistence.Repositories;

public sealed class TurnFinalizationRepository : EfRepository<TurnFinalization>, ITurnFinalizationRepository
{
    public TurnFinalizationRepository(AgentRecallDbContext db) : base(db)
    {
    }

    protected override void OnAdding(TurnFinalization entity)
    {
        if (entity.CreatedAt == default)
        {
            entity.CreatedAt = DateTimeOffset.UtcNow;
        }
    }

    // Ordered by the autoincrement Id (monotonic with insertion time) rather than CreatedAt,
    // which SQLite cannot ORDER BY.
    public async Task<TurnFinalization?> FindJudgedByTurnAsync(
        string turnId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(turnId))
        {
            return null;
        }

        return await Db.Set<TurnFinalization>()
            .AsNoTracking()
            .Where(f => f.TurnId == turnId && f.DecisionSource == TurnFinalizer.JudgeDecisionSource)
            .OrderByDescending(f => f.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
