using Ordering.Application.Abstractions;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// <see cref="IUnitOfWork"/> over the EF transaction. Commit/rollback are no-ops without an active
/// transaction so a handler may end one early (the idempotency replay path) without breaking
/// <c>TransactionBehavior</c>.
/// </summary>
public sealed class UnitOfWork(OrderingDbContext context) : IUnitOfWork
{
    public async Task BeginTransactionAsync(CancellationToken cancellationToken) =>
        await context.Database.BeginTransactionAsync(cancellationToken);

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is { } transaction)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is { } transaction)
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        context.SaveChangesAsync(cancellationToken);
}
