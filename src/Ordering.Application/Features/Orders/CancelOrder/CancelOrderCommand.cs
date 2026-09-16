using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Caching;
using Ordering.Application.Abstractions.Messaging;
using Ordering.Application.Abstractions.Persistence;
using Ordering.Application.Features.Orders.GetOrderById;
using Ordering.Application.Features.Products;
using Ordering.Domain.Orders;

namespace Ordering.Application.Features.Orders.CancelOrder;

/// <summary>
/// <c>POST /api/orders/{id}/cancel</c>. No body, no validator: the route constraint already makes
/// the id a <c>long</c>, and an id that cannot exist is simply <c>404</c>.
/// </summary>
public sealed record CancelOrderCommand(long OrderId) : ICommand<OrderDetailDto>;

/// <summary>
/// One transaction (opened by <c>TransactionBehavior</c>, which also sets <c>LOCK_TIMEOUT</c>).
/// The guarded <c>UPDATE ... WHERE status = 'Confirmed'</c> is the whole exactly-once story: the
/// status column is the lock, and of any number of racing cancels exactly one sees a row
/// affected. Only that one restores stock — in product-code order, the lock order create deducts
/// in, so a cancel racing a create on overlapping products cannot deadlock — as the last
/// statements before commit. Everyone else touches nothing and answers from a committed read.
/// Nothing is decided in C# before the update; after the commit — and only for the one cancel
/// that committed — the catalogue cache is evicted and a change hint is published, both
/// best-effort through <see cref="IUnitOfWork.OnCommitted"/>.
/// </summary>
internal sealed class CancelOrderCommandHandler(
    IUnitOfWork unitOfWork,
    IClock clock,
    IOrderRepository orders,
    IStockRepository stock,
    IOrderQueryRepository orderQuery,
    ICatalogueCache catalogue,
    IChangeHintPublisher changeHints)
    : BaseCommandHandler<CancelOrderCommand, OrderDetailDto>(unitOfWork, clock)
{
    protected override async Task<Result<OrderDetailDto>> HandleCore(CancelOrderCommand command, CancellationToken cancellationToken)
    {
        // 1. The gate. A racing cancel blocks on the order row until we commit or roll back, then
        //    re-evaluates the WHERE against 'Cancelled' and gets zero rows.
        int cancelled;

        try
        {
            cancelled = await orders.TryCancelAsync(command.OrderId, Clock.UtcNow, cancellationToken);
        }
        catch (LockTimeoutException exception) when (exception.Resource == IOrderRepository.LockedResource)
        {
            return OrderErrors.OrderBusy(command.OrderId);
        }

        if (cancelled == 0)
        {
            // 3. We did not cancel it. One committed read (the read side's own connection — the
            //    winner has committed by now, or there never was a row) tells idempotent 200 from 404.
            return await orderQuery.GetByIdAsync(command.OrderId, cancellationToken) is { } current
                ? current
                : OrderErrors.NotFound(command.OrderId);
        }

        // 2. We cancelled it. Read the order back on our own connection — it already shows
        //    'Cancelled' and its lines arrive in product-code order — then give the stock back,
        //    one statement per line, last before commit. A product row locked past the timeout
        //    rolls the whole thing back, status flip included: nothing is ever half cancelled.
        var order = await orders.ReadBackAsync(command.OrderId, cancellationToken);

        foreach (var line in order.Lines)
        {
            int restored;

            try
            {
                restored = await stock.RestoreAsync(line.ProductCode, line.Quantity, cancellationToken);
            }
            catch (LockTimeoutException exception) when (exception.Resource == IStockRepository.LockedResource)
            {
                return OrderErrors.StockBusy(line.ProductCode);
            }

            if (restored != 1)
            {
                // fk_order_lines_products forbids this; it is a broken catalogue, not a business outcome.
                throw new InvalidOperationException(
                    $"Restoring {line.Quantity} unit(s) of '{line.ProductCode}' for order {order.Id} affected {restored} product rows; expected exactly one.");
            }
        }

        // After the commit, never inside it: stock changed, so every cached catalogue page is
        // stale; the order changed, so every open event stream for it should re-read.
        UnitOfWork.OnCommitted(catalogue.InvalidateAsync);
        UnitOfWork.OnCommitted(ct => changeHints.OrderChangedAsync(order.Id, ct));
        // Task 14: UnitOfWork.OnCommitted(...) — release the restored quantities in the Redis stock gate.

        // Success: TransactionBehavior commits. Nothing is tracked, so the base class's final save flushes nothing.
        return order;
    }
}
