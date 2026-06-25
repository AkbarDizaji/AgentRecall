using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;

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
}
