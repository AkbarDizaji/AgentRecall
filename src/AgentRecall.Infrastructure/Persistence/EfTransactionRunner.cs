using AgentRecall.Core.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AgentRecall.Infrastructure.Persistence;

/// <summary>
/// <see cref="ITransactionRunner"/> over the scoped <see cref="AgentRecallDbContext"/>.
/// The repositories share this context, so a transaction opened here spans every
/// SaveChanges they perform inside the action — committing on success and rolling back on
/// any exception. Reentrant: if a transaction is already open it just runs the action so
/// the outermost call owns the commit.
/// </summary>
public sealed class EfTransactionRunner : ITransactionRunner
{
    private readonly AgentRecallDbContext _db;

    public EfTransactionRunner(AgentRecallDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Already inside a transaction (nested call): let the outer scope commit.
        if (_db.Database.CurrentTransaction is not null)
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunAsync(async ct =>
        {
            await action(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }
}
