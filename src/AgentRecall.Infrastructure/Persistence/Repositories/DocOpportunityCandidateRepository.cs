using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgentRecall.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core persistence for <see cref="DocOpportunityCandidate"/>. Stamps the timestamp on
/// insert and exposes the newest-first reads the <c>document</c> commands use.
/// </summary>
public sealed class DocOpportunityCandidateRepository : EfRepository<DocOpportunityCandidate>, IDocOpportunityCandidateRepository
{
    public DocOpportunityCandidateRepository(AgentRecallDbContext db) : base(db)
    {
    }

    protected override void OnAdding(DocOpportunityCandidate entity)
    {
        if (entity.CreatedAt == default)
        {
            entity.CreatedAt = DateTimeOffset.UtcNow;
        }
    }

    // Append-only, so the autoincrement Id is monotonic with insertion time. Ordering by Id
    // (rather than the stored DateTimeOffset, which SQLite cannot ORDER BY) gives newest-first
    // results efficiently in SQL.
    public async Task<DocOpportunityCandidate?> GetLatestAsync(CancellationToken cancellationToken = default) =>
        await Db.Set<DocOpportunityCandidate>()
            .AsNoTracking()
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<DocOpportunityCandidate?> FindByOperationHashAsync(string operationHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(operationHash))
        {
            return null;
        }

        return await Db.Set<DocOpportunityCandidate>()
            .AsNoTracking()
            .Where(c => c.OperationHash == operationHash)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DocOpportunityCandidate?> FindByTurnAsync(string turnId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(turnId))
        {
            return null;
        }

        return await Db.Set<DocOpportunityCandidate>()
            .AsNoTracking()
            .Where(c => c.TurnId == turnId)
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
