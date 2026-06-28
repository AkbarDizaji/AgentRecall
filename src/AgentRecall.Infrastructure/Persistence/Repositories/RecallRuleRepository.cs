using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Infrastructure.Persistence.Repositories;

public sealed class RecallRuleRepository : EfRepository<RecallRule>, IRecallRuleRepository
{
    public RecallRuleRepository(AgentRecallDbContext db) : base(db)
    {
    }

    protected override void OnAdding(RecallRule entity)
    {
        var now = DateTimeOffset.UtcNow;
        if (entity.CreatedAt == default)
        {
            entity.CreatedAt = now;
        }

        entity.UpdatedAt = now;

        if (entity.Version <= 0)
        {
            entity.Version = 1;
        }
    }

    protected override void OnUpdating(RecallRule entity) =>
        entity.UpdatedAt = DateTimeOffset.UtcNow;
}
