using AgentRecall.Core.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AgentRecall.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core base implementation of <see cref="IRepository{T}"/>. Subclasses may
/// override <see cref="OnAdding"/> to stamp timestamps before insert.
/// </summary>
public abstract class EfRepository<T> : IRepository<T> where T : class
{
    protected readonly AgentRecallDbContext Db;

    protected EfRepository(AgentRecallDbContext db)
    {
        Db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public virtual async Task<T> AddAsync(T entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        OnAdding(entity);
        await Db.Set<T>().AddAsync(entity, cancellationToken).ConfigureAwait(false);
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entity;
    }

    public virtual Task<T?> GetAsync(int id, CancellationToken cancellationToken = default) =>
        Db.Set<T>().FindAsync([id], cancellationToken).AsTask();

    public virtual async Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default) =>
        await Db.Set<T>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

    public virtual async Task<T> UpdateAsync(T entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        Db.Set<T>().Update(entity);
        await Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entity;
    }

    /// <summary>Hook invoked before an entity is added; default is a no-op.</summary>
    protected virtual void OnAdding(T entity)
    {
    }
}
