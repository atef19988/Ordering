using System.Text.RegularExpressions;
using FluentValidation;
using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Messaging;
using Ordering.Application.Abstractions.Outbox;
using Ordering.Application.Features.Orders.GetOrderById;
using Ordering.Application.Features.Products;
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

/// <param name="IdempotencyKey">The <c>Idempotency-Key</c> header. Its shape is validated here; Task 5 stores it.</param>
public sealed record CreateOrderCommand(string IdempotencyKey, string CustomerReference, IReadOnlyList<CreateOrderLine> Lines)
    : ICommand<OrderDetailDto>;

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
/// One transaction (opened by <c>TransactionBehavior</c>): conditional stock decrement per line,
/// then order + lines + outbox row in one <c>SaveChanges</c>. Any failure result or exception rolls
/// the whole thing back, so a 409 on the third line restores the first two automatically.
/// </summary>
internal sealed class CreateOrderCommandHandler(
    IUnitOfWork unitOfWork,
    IClock clock,
    IIdGenerator ids,
    IProductRepository products,
    IStockRepository stock,
    IOrderRepository orders,
    IOutbox outbox)
    : BaseCommandHandler<CreateOrderCommand, OrderDetailDto>(unitOfWork, clock)
{
    protected override async Task<Result<OrderDetailDto>> HandleCore(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        // Task 5: SET LOCK_TIMEOUT 3000 first, then REORDER this handler — stage order + lines + outbox
        // + idempotency row and SaveChanges BEFORE the stock decrements, so the product row lock is
        // held for one round trip and a duplicate key conflicts before touching stock (spec §Flow).

        var requested = command.Lines.ToDictionary(l => ProductCodes.Normalize(l.ProductCode), l => l.Quantity, ProductCodes.Comparer);

        // 1. Prices come from the catalogue, never from the request. The rows arrive in database
        //    code order — the lock order every writer uses — so the decrements below follow it.
        var catalogue = await products.GetByCodesAsync(requested.Keys, cancellationToken);

        if (catalogue.Count < requested.Count)
        {
            var unknown = requested.Keys.First(code => !catalogue.Any(p => ProductCodes.Comparer.Equals(p.Code, code)));
            return OrderErrors.UnknownProduct(unknown);
        }

        var orderId = ids.NewId();
        var now = Clock.UtcNow;
        var lines = new List<OrderLine>(catalogue.Count);

        foreach (var product in catalogue)
        {
            var quantity = requested[product.Code];

            // 2. The conditional UPDATE is the whole concurrency story: zero rows affected means the
            //    database refused because another committed order got there first.
            if (await stock.TryDeductAsync(product.Code, quantity, cancellationToken) == 0)
            {
                return OrderErrors.InsufficientStock(product.Code, await stock.GetAvailableQuantityAsync(product.Code, cancellationToken));
            }

            lines.Add(OrderLine.Create(ids.NewId(), orderId, product.Code, quantity, product.Price));
        }

        // 3–4. The domain computes the total; EF inserts order and lines with the outbox row below.
        var order = Order.Create(orderId, command.CustomerReference.Trim(), lines, now);
        orders.Add(order);

        // 5. The notification is staged in the same unit of work; its Snowflake id is the stable event id.
        outbox.Enqueue(OutboxMessage.Create(
            ids.NewId(),
            OrderCreated.EventType,
            order.Id,
            new OrderCreated(order.Id, order.CustomerReference, order.Total, now),
            now));

        // Task 5: the idempotency_keys row joins the SaveChanges above (same batch as the order), and a
        // pk_idempotency_keys violation becomes the replay / key-reuse outcome (200 vs 409); a stock 409
        // after it rolls the key row back too, so a conflict never consumes the key.

        return ToDto(order);
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
