using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Infrastructure.Persistence.Repositories;

public sealed class RuleLifecycleRecommendationRepository
    : EfRepository<RuleLifecycleRecommendation>, IRuleLifecycleRecommendationRepository
{
    public RuleLifecycleRecommendationRepository(AgentRecallDbContext db) : base(db)
    {
    }

    protected override void OnAdding(RuleLifecycleRecommendation entity)
    {
        var now = DateTimeOffset.UtcNow;
        if (entity.CreatedAt == default)
        {
            entity.CreatedAt = now;
        }

        entity.UpdatedAt = now;
    }
}
