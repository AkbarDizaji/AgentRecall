namespace AgentRecall.Core.Abstractions;

/// <summary>
/// Runs a multi-step unit of work atomically: every repository write inside the action
/// commits together, or none of them do. Lets a Core service make a sequence of mutations
/// transactional without depending on EF Core or the DbContext directly.
/// </summary>
public interface ITransactionRunner
{
    /// <summary>Runs <paramref name="action"/> in a transaction, returning its result.</summary>
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);

    /// <summary>Runs <paramref name="action"/> in a transaction.</summary>
    Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default);
}
