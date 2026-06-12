using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Infrastructure.Persistence.Repositories;

public sealed class RecallEventRepository : EfRepository<RecallEvent>, IRecallEventRepository
{
    public RecallEventRepository(AgentRecallDbContext db) : base(db)
    {
    }

    protected override void OnAdding(RecallEvent entity)
    {
        if (entity.CreatedAt == default)
        {
            entity.CreatedAt = DateTimeOffset.UtcNow;
        }
    }
}
