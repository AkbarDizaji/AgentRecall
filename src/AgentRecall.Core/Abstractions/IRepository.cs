namespace AgentRecall.Core.Abstractions;

/// <summary>
/// Minimal async persistence contract shared by the Phase 2 repositories.
/// </summary>
public interface IRepository<T> where T : class
{
    /// <summary>Adds a new entity and returns it with its generated id populated.</summary>
    Task<T> AddAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>Returns the entity with the given id, or null if not found.</summary>
    Task<T?> GetAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Returns all entities.</summary>
    Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default);
}
