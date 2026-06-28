using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Infrastructure.Persistence.Repositories;

public sealed class LessonCandidateRepository : EfRepository<LessonCandidate>, ILessonCandidateRepository
{
    public LessonCandidateRepository(AgentRecallDbContext db) : base(db)
    {
    }

    protected override void OnAdding(LessonCandidate entity)
    {
        var now = DateTimeOffset.UtcNow;
        if (entity.CreatedAt == default)
        {
            entity.CreatedAt = now;
        }

        entity.UpdatedAt = now;
    }

    protected override void OnUpdating(LessonCandidate entity) =>
        entity.UpdatedAt = DateTimeOffset.UtcNow;
}
