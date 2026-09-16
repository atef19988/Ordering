using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Ordering.Application.Abstractions;
using Ordering.Application.Idempotency;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// <see cref="IUnitOfWork"/> over the EF transaction. Commit/rollback are no-ops without an active
/// transaction so a handler may end one early (the idempotency replay path) without breaking
/// <c>TransactionBehavior</c>.
/// </summary>
public sealed class UnitOfWork(OrderingDbContext context) : IUnitOfWork
{
    private readonly List<Func<CancellationToken, Task>> _committedActions = [];

    public async Task BeginTransactionAsync(CancellationToken cancellationToken) =>
        await context.Database.BeginTransactionAsync(cancellationToken);

    /// <summary>
    /// <c>SET LOCK_TIMEOUT</c> is connection-scoped and pooled connections are reset between
    /// uses, so it is issued on the transaction's own connection every time.
    /// </summary>
    public Task SetLockTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // SET takes a literal, not a parameter; the value is an integer we formatted ourselves.
        var milliseconds = ((long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        return context.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT " + milliseconds + ";", cancellationToken);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is { } transaction)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Also empties the change tracker — rows staged before the rollback must not be re-inserted
    /// by the base handler's final save when the handler goes on to return a success (a replay) —
    /// and forgets the post-commit actions: nothing committed, so nothing follows.
    /// </summary>
    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is { } transaction)
        {
            await transaction.RollbackAsync(cancellationToken);
        }

        context.ChangeTracker.Clear();
        _committedActions.Clear();
    }

    public void OnCommitted(Func<CancellationToken, Task> action) => _committedActions.Add(action);

    public IReadOnlyList<Func<CancellationToken, Task>> TakeCommittedActions()
    {
        var actions = _committedActions.ToArray();
        _committedActions.Clear();
        return actions;
    }

    /// <summary>
    /// The only lock the create batch can wait on is <c>pk_idempotency_keys</c> (every other key
    /// it inserts is a fresh Snowflake), so a timeout here is reported against that table.
    /// </summary>
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (SqlErrors.TryTranslate(exception, IdempotencyKey.TableName, out var translated))
        {
            throw translated;
        }
    }
}
