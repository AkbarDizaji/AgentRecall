using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Infrastructure.Persistence.Repositories;

public sealed class RetrievalRecordRepository : EfRepository<RetrievalRecord>, IRetrievalRecordRepository
{
    public RetrievalRecordRepository(AgentRecallDbContext db) : base(db)
    {
    }

    protected override void OnAdding(RetrievalRecord entity)
    {
        if (entity.CreatedAt == default)
        {
            entity.CreatedAt = DateTimeOffset.UtcNow;
        }
    }
}
