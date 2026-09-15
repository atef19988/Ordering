# Task 3 — Read side with Dapper

Queries never touch EF Core, never open a transaction, never mutate.

## Queries — `Ordering.Application/Features/`

```
Products/GetProducts/GetProductsQuery.cs    -> IReadOnlyList<ProductDto>
Orders/GetOrderById/GetOrderByIdQuery.cs    -> OrderDetailDto (Result.NotFound if absent)
```

DTOs are flat records in the same file, shaped for the wire:

```csharp
public sealed record ProductDto(string Code, string Name, decimal Price, int AvailableQuantity);

public sealed record OrderLineDto(string ProductCode, int Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record OrderDetailDto(
    long Id, string CustomerReference, string Status, decimal Total,   // Snowflake; JSON string on the wire
    DateTimeOffset CreatedAt, DateTimeOffset? CancelledAt,
    string NotificationStatus, int NotificationAttempts,
    IReadOnlyList<OrderLineDto> Lines);
```

## Repositories — `Ordering.Infrastructure/Read/`

`ProductQueryRepository` and `OrderQueryRepository`, both extending `BaseDapperRepository`.
SQL is parameterized T-SQL, explicit column lists, no `SELECT *`.

Order detail is **one round trip** using `QueryMultipleAsync` (two result sets in one batch):

```sql
SELECT o.id, o.customer_reference, o.status, o.total, o.created_at, o.cancelled_at,
       COALESCE(ob.status, 'None')   AS notification_status,
       COALESCE(ob.attempt_count, 0) AS notification_attempts
  FROM orders AS o
  LEFT JOIN outbox_messages AS ob
         ON ob.aggregate_id = o.id AND ob.type = 'order.created'
 WHERE o.id = @id;

SELECT product_code, quantity, unit_price, line_total
  FROM order_lines
 WHERE order_id = @id
 ORDER BY product_code;
```

Map outbox status to the API vocabulary in one place: `Pending|Processing -> Pending`,
`Sent -> Sent`, `Failed -> Failed`.

> `outbox_messages` is created in Task 4. Until then, write the join behind the same repository
> method and let Task 4 make it live — do not duplicate the query later.

## Endpoints

| Method | Route | Returns |
|---|---|---|
| GET | `/api/products` | 200 `ProductDto[]` |
| GET | `/api/orders/{id}` | 200 `OrderDetailDto`, 404 if unknown |

Endpoints live in `Api/Endpoints/ProductsEndpoints.cs` and `OrdersEndpoints.cs` (Minimal API,
`MapProducts()` / `MapOrders()` on the `/api` group), bind `{id:long}` from the route, dispatch,
and return `result.ToHttpResult()`. Nothing else.

## Definition of done

- [ ] Integration test: seeded products come back with exact decimal prices (12.50, not 12.5000001)
- [ ] Integration test: unknown order id → 404 ProblemDetails
- [ ] Order detail returns lines ordered by product code and a `notificationStatus`
- [ ] No EF `DbContext` reference anywhere in `Infrastructure/Read`
