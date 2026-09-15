# Task 2 — Domain, EF Core write model, constraints, seed

## Domain — `Ordering.Domain/`

```
Products/Product.cs        Code (PK-ish natural key), Name, Price (decimal), AvailableQuantity
Orders/Order.cs            Id (long, Snowflake), CustomerReference, Status, Total, CreatedAt, CancelledAt, Lines
Orders/OrderLine.cs        Id (long), OrderId, ProductCode, Quantity, UnitPrice, LineTotal
Orders/OrderStatus.cs      Confirmed | Cancelled
Orders/OrderErrors.cs      static Error factories: InsufficientStock(code, available),
                           NotFound(id), AlreadyCancelled(id), UnknownProduct(code)
```

Rules that live in the domain (and get unit tests):

- `Order.Create(id, customerRef, lines, now)` computes `Total = Σ(quantity * unitPrice)` rounded
  to 2dp. **Unit price always comes from the database**, never from the request. Ids are passed
  in from `IIdGenerator` — the domain never generates them and the database never assigns them.
- Quantity must be `> 0`; customer reference non-empty, ≤ 64 chars.
- `Order.Cancel(now)` is only legal from `Confirmed`.

Entities have private setters and no EF attributes. Configuration lives in Infrastructure.

## EF Core — `Ordering.Infrastructure/Persistence/`

`OrderingDbContext` + `IEntityTypeConfiguration` per entity, provider
`Microsoft.EntityFrameworkCore.SqlServer` 9.x. Snake_case table/column naming. Key columns use
`ValueGeneratedNever()` — ids are Snowflake values set in code.

Migration `Initial` must create:

```sql
CREATE TABLE products (
    code               nvarchar(32)  NOT NULL CONSTRAINT pk_products PRIMARY KEY,
    name               nvarchar(200) NOT NULL,
    price              decimal(18,2) NOT NULL CONSTRAINT ck_products_price_non_negative CHECK (price >= 0),
    available_quantity int           NOT NULL CONSTRAINT ck_products_qty_non_negative CHECK (available_quantity >= 0)
);

CREATE TABLE orders (
    id                 bigint         NOT NULL CONSTRAINT pk_orders PRIMARY KEY,   -- Snowflake, clustered, ascending
    customer_reference nvarchar(64)   NOT NULL,
    status             nvarchar(16)   NOT NULL,
    total              decimal(18,2)  NOT NULL,
    created_at         datetimeoffset NOT NULL,
    cancelled_at       datetimeoffset NULL,
    row_version        rowversion     NOT NULL                                     -- EF concurrency token
);

CREATE TABLE order_lines (
    id           bigint        NOT NULL CONSTRAINT pk_order_lines PRIMARY KEY,
    order_id     bigint        NOT NULL CONSTRAINT fk_order_lines_orders   REFERENCES orders(id) ON DELETE CASCADE,
    product_code nvarchar(32)  NOT NULL CONSTRAINT fk_order_lines_products REFERENCES products(code),
    quantity     int           NOT NULL CONSTRAINT ck_order_lines_qty_positive CHECK (quantity > 0),
    unit_price   decimal(18,2) NOT NULL,
    line_total   decimal(18,2) NOT NULL
);
CREATE INDEX ix_order_lines_order_id ON order_lines(order_id);
```

Map `decimal` with `HasPrecision(18,2)`. Map `row_version` via `.IsRowVersion()` on `Order`.
Map every `DateTimeOffset` to `datetimeoffset`.

## Seed

`DbInitializer.SeedAsync` — idempotent (`INSERT ... WHERE NOT EXISTS (SELECT 1 FROM products
WHERE code = @code)`, one statement per product, no `MERGE`), runs on startup in Development and
from `dotnet run -- seed`:

| code | name | price | quantity |
|---|---|---|---|
| SKU-001 | Thermal label roll | 12.50 | 10 |
| SKU-002 | Shipping tape | 4.00 | 40 |

## Definition of done

- [x] `dotnet ef migrations add Initial` applied to a real SQL Server, `dotnet ef database update` clean
- [x] Attempting `UPDATE products SET available_quantity = -1` is rejected by the check constraint
- [x] Unit tests: total calculation with 2dp rounding, quantity ≤ 0 rejected, cancel-from-cancelled rejected
- [x] Re-running seed twice leaves exactly two products
