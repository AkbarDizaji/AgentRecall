using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Infrastructure.Persistence.Repositories;

public sealed class RecallScopeRepository : EfRepository<RecallScope>, IRecallScopeRepository
{
    public RecallScopeRepository(AgentRecallDbContext db) : base(db)
    {
    }

    protected override void OnAdding(RecallScope entity)
    {
        if (entity.CreatedAt == default)
        {
            entity.CreatedAt = DateTimeOffset.UtcNow;
        }
    }
}
