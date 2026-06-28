using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgentRecall.Infrastructure.Persistence.Repositories;

public sealed class RecallRuleRepository : EfRepository<RecallRule>, IRecallRuleRepository
{
    public RecallRuleRepository(AgentRecallDbContext db) : base(db)
    {
    }

    public async Task<IReadOnlyList<RecallRule>> QueryAsync(RuleQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IQueryable<RecallRule> q = Db.Set<RecallRule>().AsNoTracking();

        if (query.ScopeLevel is { } level)
        {
            q = q.Where(r => r.ScopeLevel == level);
        }

        if (query.ScopeValue is { } scopeValue)
        {
            // Case-insensitive exact match; lower() is translatable by the SQLite provider.
            var lowered = scopeValue.ToLower();
            q = q.Where(r => (r.ScopeValue ?? string.Empty).ToLower() == lowered);
        }

        if (query.Statuses is { Count: > 0 } statuses)
        {
            q = q.Where(r => statuses.Contains(r.Status));
        }

        if (query.ExcludeStatuses is { Count: > 0 } excluded)
        {
            q = q.Where(r => !excluded.Contains(r.Status));
        }

        if (query.Deprecated is { } deprecated)
        {
            q = q.Where(r => r.Deprecated == deprecated);
        }

        return await q.ToListAsync(cancellationToken).ConfigureAwait(false);
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
