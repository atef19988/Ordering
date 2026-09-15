namespace Ordering.Application.Abstractions;

/// <summary>
/// The write-side transaction boundary. Implemented over EF Core in Infrastructure (Task 2).
/// </summary>
public interface IUnitOfWork
{
    Task BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Commits the current transaction. A no-op when none is active.</summary>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>Rolls back the current transaction. A no-op when none is active.</summary>
    Task RollbackAsync(CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
