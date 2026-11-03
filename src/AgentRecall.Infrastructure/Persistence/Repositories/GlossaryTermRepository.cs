using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgentRecall.Infrastructure.Persistence.Repositories;

public sealed class GlossaryTermRepository : EfRepository<GlossaryTerm>, IGlossaryTermRepository
{
    public GlossaryTermRepository(AgentRecallDbContext db) : base(db)
    {
    }

    public Task<GlossaryTerm?> FindByTermAsync(string normalizedTerm, CancellationToken cancellationToken = default) =>
        Db.GlossaryTerms.FirstOrDefaultAsync(t => t.Term == normalizedTerm, cancellationToken);

    protected override void OnAdding(GlossaryTerm entity)
    {
        if (entity.CreatedAt == default)
        {
            entity.CreatedAt = DateTimeOffset.UtcNow;
        }
    }
}
