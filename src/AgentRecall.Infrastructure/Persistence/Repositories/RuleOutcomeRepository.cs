using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Infrastructure.Persistence.Repositories;

public sealed class RuleOutcomeRepository : EfRepository<RuleOutcome>, IRuleOutcomeRepository
{
    public RuleOutcomeRepository(AgentRecallDbContext db) : base(db)
    {
    }

    protected override void OnAdding(RuleOutcome entity)
    {
        if (entity.CreatedAt == default)
        {
            entity.CreatedAt = DateTimeOffset.UtcNow;
        }
    }
}
