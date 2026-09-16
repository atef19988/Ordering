using System.Text.RegularExpressions;
using FluentValidation;
using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Caching;
using Ordering.Application.Abstractions.Messaging;
using Ordering.Application.Abstractions.Outbox;
using Ordering.Application.Abstractions.Persistence;
using Ordering.Application.Features.Orders.GetOrderById;
using Ordering.Application.Features.Products;
using Ordering.Application.Idempotency;
using Ordering.Domain.Orders;
using Ordering.Domain.Products;

namespace Ordering.Application.Features.Orders.CreateOrder;

public sealed record CreateOrderLine(string ProductCode, int Quantity);

/// <summary>
/// The body of <c>POST /api/orders</c>. It carries no price: anything else the client sends is
/// ignored by the JSON binder and the total is computed from catalogue prices.
/// </summary>
public sealed record CreateOrderRequest(string CustomerReference, IReadOnlyList<CreateOrderLine> Lines)
{
    public CreateOrderCommand ToCommand(string? idempotencyKey) =>
        new(idempotencyKey ?? string.Empty, CustomerReference, Lines);
}

/// <param name="IdempotencyKey">The <c>Idempotency-Key</c> header; validated here, stored with the order.</param>
public sealed record CreateOrderCommand(string IdempotencyKey, string CustomerReference, IReadOnlyList<CreateOrderLine> Lines)
    : ICommand<CreateOrderResponse>;

/// <param name="Order">The order as <c>GET /api/orders/{id}</c> reports it right now.</param>
/// <param name="Replayed">
/// <c>true</c> when the key was already committed with an equivalent payload and nothing was
/// written. The endpoint maps it to <c>200 OK</c> instead of <c>201 Created</c> — the only branch
/// it is allowed to have. Task 14's admission behaviour reads the same flag.
/// </param>
public sealed record CreateOrderResponse(OrderDetailDto Order, bool Replayed);

internal sealed partial class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator()
    {
        RuleFor(c => c.IdempotencyKey)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("The Idempotency-Key header is required.")
            .Must(BeGuidOrUlidShaped).WithMessage("The Idempotency-Key header must be a GUID or a ULID.")
            .OverridePropertyName("Idempotency-Key");

        RuleFor(c => c.CustomerReference)
            .NotEmpty()
            .MaximumLength(Order.CustomerReferenceMaxLength);

        RuleFor(c => c.Lines)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("At least one line is required.")
            .Must(HaveDistinctProductCodes).WithMessage("A product may appear only once per order; merge its quantities into one line.");

        RuleForEach(c => c.Lines)
            .NotNull().WithMessage("A line must be an object with productCode and quantity.")
            .ChildRules(line =>
            {
                line.RuleFor(l => l.ProductCode).NotEmpty().MaximumLength(Product.CodeMaxLength);
                line.RuleFor(l => l.Quantity).GreaterThanOrEqualTo(1);
            });
    }

    private static bool BeGuidOrUlidShaped(string key) => Guid.TryParse(key, out _) || Ulid().IsMatch(key);

    private static bool HaveDistinctProductCodes(IReadOnlyList<CreateOrderLine> lines)
    {
        var codes = lines.Where(l => l?.ProductCode is not null).Select(l => ProductCodes.Normalize(l.ProductCode)).ToList();
        return codes.Distinct(ProductCodes.Comparer).Count() == codes.Count;
    }

    // Crockford base32, 26 characters (no I, L, O, U).
    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{26}$", RegexOptions.IgnoreCase)]
    private static partial Regex Ulid();
}

/// <summary>
/// One transaction (opened by <c>TransactionBehavior</c>, which also sets <c>LOCK_TIMEOUT</c>):
/// catalogue read, then order + lines + outbox row + idempotency key in one <c>SaveChanges</c>,
/// then the conditional stock decrement per line as the <b>last</b> statement before commit. A
/// duplicate key therefore conflicts on <c>pk_idempotency_keys</c> before it touches a product
/// row, and a hot product row is exclusively locked for one round trip plus the commit. Any
/// failure result or exception rolls the whole thing back — including the key row, so a stock
/// conflict never consumes the key.
/// </summary>
internal sealed class CreateOrderCommandHandler(
    IUnitOfWork unitOfWork,
    IClock clock,
    IIdGenerator ids,
    IProductRepository products,
    IStockRepository stock,
    IOrderRepository orders,
    IOutbox outbox,
    IIdempotencyStore idempotencyKeys,
    IOrderQueryRepository orderQuery,
    ICatalogueCache catalogueCache)
    : BaseCommandHandler<CreateOrderCommand, CreateOrderResponse>(unitOfWork, clock)
{
    protected override async Task<Result<CreateOrderResponse>> HandleCore(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        var requested = command.Lines.ToDictionary(l => ProductCodes.Normalize(l.ProductCode), l => l.Quantity, ProductCodes.Comparer);

        // 1. Prices come from the catalogue, never from the request. The rows arrive in database
        //    code order — the lock order every writer uses — so the decrements below follow it.
        var catalogue = await products.GetByCodesAsync(requested.Keys, cancellationToken);

        if (catalogue.Count < requested.Count)
        {
            var unknown = requested.Keys.First(code => !catalogue.Any(p => ProductCodes.Comparer.Equals(p.Code, code)));
            return OrderErrors.UnknownProduct(unknown);
        }

        // 2. Build the aggregate in memory; the domain computes the total.
        var orderId = ids.NewId();
        var now = Clock.UtcNow;
        var lines = catalogue
            .Select(product => OrderLine.Create(ids.NewId(), orderId, product.Code, requested[product.Code], product.Price))
            .ToList();
        var order = Order.Create(orderId, command.CustomerReference.Trim(), lines, now);

        // 3. Stage everything that needs no product row lock: order, lines, the notification (its
        //    Snowflake id is the stable event id) and the idempotency key.
        orders.Add(order);
        outbox.Enqueue(OutboxMessage.Create(ids.NewId(), OrderCreated.EventType, order.Id, new OrderCreated(order.Id, order.CustomerReference, order.Total, now), now));
        idempotencyKeys.Add(new IdempotencyKey(command.IdempotencyKey, RequestFingerprint.Compute(command), order.Id, now));

        // 4. One batch. The primary key on idempotency_keys is the idempotency check: a duplicate
        //    that is still in flight blocks here until the first transaction ends, then conflicts.
        try
        {
            await UnitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueViolationException exception) when (exception.ConstraintName == IdempotencyKey.PrimaryKeyConstraint)
        {
            await UnitOfWork.RollbackAsync(cancellationToken);
            return await ReplayAsync(command, cancellationToken);
        }
        catch (LockTimeoutException exception) when (exception.Resource == IdempotencyKey.TableName)
        {
            return OrderErrors.IdempotencyInProgress(command.IdempotencyKey);
        }

        // 5. The conditional UPDATE is the whole concurrency story: zero rows affected means the
        //    database refused because another committed order got there first. Last before commit.
        foreach (var line in order.Lines)
        {
            int deducted;

            try
            {
                deducted = await stock.TryDeductAsync(line.ProductCode, line.Quantity, cancellationToken);
            }
            catch (LockTimeoutException exception) when (exception.Resource == IStockRepository.LockedResource)
            {
                return OrderErrors.StockBusy(line.ProductCode);
            }

            if (deducted == 0)
            {
                return OrderErrors.InsufficientStock(line.ProductCode, await stock.GetAvailableQuantityAsync(line.ProductCode, cancellationToken));
            }
        }

        // 6. Success: TransactionBehavior commits, then evicts the catalogue pages that still show
        //    the old stock (never inside the transaction). No change hint: the caller holds the 201
        //    body, and nobody can be streaming an order that does not exist yet.
        UnitOfWork.OnCommitted(catalogueCache.InvalidateAsync);
        // Task 14: UnitOfWork.OnCommitted(...) — confirm the gate reservation as consumed.
        return new CreateOrderResponse(ToDto(order), Replayed: false);
    }

    /// <summary>
    /// The key is already committed. Same payload → the stored order in its <em>current</em>
    /// state (it may be cancelled by now); different payload → 409. Runs after the rollback, on
    /// the read side's own connections.
    /// </summary>
    private async Task<Result<CreateOrderResponse>> ReplayAsync(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        var existing = await idempotencyKeys.FindAsync(command.IdempotencyKey, cancellationToken);

        if (existing is null)
        {
            // The row we collided with is gone (its transaction ended without committing after all): retry.
            return OrderErrors.IdempotencyInProgress(command.IdempotencyKey);
        }

        if (!string.Equals(existing.RequestHash, RequestFingerprint.Compute(command), StringComparison.Ordinal))
        {
            return OrderErrors.IdempotencyKeyReuse(command.IdempotencyKey);
        }

        var order = await orderQuery.GetByIdAsync(existing.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Idempotency key '{command.IdempotencyKey}' references order {existing.OrderId}, which does not exist.");

        return new CreateOrderResponse(order, Replayed: true);
    }

    /// <summary>
    /// What <c>GET /api/orders/{id}</c> will return once the transaction commits; built in memory
    /// because a Dapper read on a second connection would block on our own uncommitted rows.
    /// </summary>
    private static OrderDetailDto ToDto(Order order) => new(
        order.Id,
        order.CustomerReference,
        order.Status.ToString(),
        order.Total,
        order.CreatedAt,
        order.CancelledAt,
        NotificationStatus.Pending,
        NotificationAttempts: 0,
        order.Lines.Select(line => new OrderLineDto(line.ProductCode, line.Quantity, line.UnitPrice, line.LineTotal)).ToList());
}

/// <summary>
/// Product codes are matched the way SQL Server's default collation matches them: trailing
/// whitespace and letter case do not distinguish two codes. Validator and handler share this rule.
/// </summary>
file static class ProductCodes
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static string Normalize(string code) => code.Trim();
}
