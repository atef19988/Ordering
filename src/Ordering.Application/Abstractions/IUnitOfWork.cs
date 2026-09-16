using Ordering.Application.Abstractions.Persistence;

namespace Ordering.Application.Abstractions;

/// <summary>
/// The write-side transaction boundary. Implemented over EF Core by <c>Infrastructure.Persistence.UnitOfWork</c>.
/// </summary>
public interface IUnitOfWork
{
    Task BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Bounds every lock wait on the transaction's connection. Connection-scoped in SQL Server, so
    /// it is issued inside each transaction; a wait that exceeds it surfaces as <see cref="LockTimeoutException"/>.
    /// </summary>
    Task SetLockTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Commits the current transaction. A no-op when none is active.</summary>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Rolls back the current transaction and forgets every staged change, so a later save flushes
    /// nothing. A no-op when no transaction is active.
    /// </summary>
    Task RollbackAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Flushes staged changes in one batch. Throws <see cref="UniqueViolationException"/> when a
    /// unique key is already taken and <see cref="LockTimeoutException"/> (<c>"idempotency_keys"</c>)
    /// when the batch waited too long for a key lock; the transaction is still open either way.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The only sanctioned place for I/O that must follow a commit (cache eviction, change hints,
    /// Task 14's gate release). <c>TransactionBehavior</c> runs the registered actions after
    /// <see cref="CommitAsync"/> returned, in registration order, each best-effort: a failure is
    /// logged and never fails the request. A rollback forgets them, so they never run for a
    /// transaction that did not commit.
    /// </summary>
    void OnCommitted(Func<CancellationToken, Task> action);

    /// <summary>Hands the registered post-commit actions to the caller and forgets them.</summary>
    IReadOnlyList<Func<CancellationToken, Task>> TakeCommittedActions();
}
