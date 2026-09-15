# Task 4 — POST /api/orders

The core of the exercise. One transaction, database-enforced stock, outbox message.

## Request

```http
POST /api/orders
Idempotency-Key: 9f1c2a7e-...          (required; 400 if missing or not a GUID/ULID-shaped string)
Content-Type: application/json

{ "customerReference": "CUST-42",
  "lines": [ { "productCode": "SKU-001", "quantity": 2 } ] }
```

Validation (400): reference required ≤64 chars, at least one line, quantity ≥ 1,
no duplicate product codes in one request (merge or reject — reject, and say so in the README).

## Handler — `Application/Features/Orders/CreateOrder/CreateOrderCommand.cs`

Inside one transaction, in this order:

1. Load prices for the requested codes (`SELECT code, price FROM products WHERE code IN @codes`
   — Dapper expands the list; or EF `Where(p => codes.Contains(p.Code))`).
   Missing code → `Error.NotFound("product.unknown")` → 404.
2. For each line, **conditional decrement** via `IStockRepository.TryDeductAsync(code, qty, ct)`:

   ```sql
   UPDATE products
      SET available_quantity = available_quantity - @qty
    WHERE code = @code AND available_quantity >= @qty;
   ```

   Returns rows affected. `0` → `OrderErrors.InsufficientStock(code, available)` → **409**, and
   the whole transaction rolls back so earlier lines are restored automatically.
   Process lines **sorted by product code** so concurrent multi-line orders take row locks in the
   same order and cannot deadlock.
3. Build the `Order` from the domain factory using DB prices and ids from `IIdGenerator.NewId()`
   (order and each line); server computes `Total`.
4. Insert order + lines via EF.
5. Insert the outbox message (Task 7 consumes it):

   ```
   id              = IIdGenerator.NewId()       (Snowflake — the stable event id)
   type            = 'order.created'
   aggregate_id    = order.Id
   payload         = {"orderId":"..","customerReference":..,"total":..,"occurredAt":..}
   status          = 'Pending'
   next_attempt_at = IClock.UtcNow
   attempt_count   = 0
   ```
6. Insert the idempotency row — Task 5 owns this step; leave a clearly marked seam.

Commit. Response `201 Created`, `Location: /api/orders/{id}`, body = `OrderDetailDto`.

## Outbox table migration (add here)

```sql
CREATE TABLE outbox_messages (
    id              bigint         NOT NULL CONSTRAINT pk_outbox_messages PRIMARY KEY,  -- Snowflake event id
    type            nvarchar(64)   NOT NULL,
    aggregate_id    bigint         NOT NULL,
    payload         nvarchar(max)  NOT NULL CONSTRAINT ck_outbox_payload_json CHECK (ISJSON(payload) = 1),
    occurred_at     datetimeoffset NOT NULL,
    status          nvarchar(16)   NOT NULL,            -- Pending|Processing|Sent|Failed
    attempt_count   int            NOT NULL CONSTRAINT df_outbox_attempts DEFAULT 0,
    next_attempt_at datetimeoffset NOT NULL,
    claimed_by      nvarchar(64)   NULL,
    claimed_until   datetimeoffset NULL,
    processed_at    datetimeoffset NULL,
    last_error      nvarchar(1000) NULL
);

CREATE UNIQUE INDEX ux_outbox_order_created
    ON outbox_messages(type, aggregate_id) WHERE type = 'order.created';   -- filtered unique index
CREATE INDEX ix_outbox_due
    ON outbox_messages(status, next_attempt_at) WHERE status IN ('Pending', 'Processing');
```

In EF: `.HasIndex(...).IsUnique().HasFilter("[type] = 'order.created'")`.

## Rules

- No `lock`, `SemaphoreSlim`, static state or `SELECT ... then UPDATE` on stock.
- No `SaveChanges` outside the transaction; no HTTP/queue call inside it.
- 409 body: `{"code":"stock.insufficient","productCode":"SKU-001","available":1}` in
  ProblemDetails `extensions`.

## Definition of done

- [ ] Integration test: 1 unit in stock, two concurrent different orders → one 201, one 409, stock = 0
- [ ] Integration test: forced exception before commit → no order, no stock change, no outbox row
- [ ] Integration test: 409 response leaves stock untouched (not partially deducted on multi-line)
- [x] Total is computed server-side even if the client sends a price field (it is ignored)

> Test code was deferred by owner instruction ("skip write test code"). The behaviours were
> exercised by hand against the compose database instead: a 50-request barrier race for the last
> unit held in 5/5 runs (1×201, 49×409, `available_quantity = 0`, one order, one outbox row —
> asserted in SQL), and a two-line order whose second line was short returned 409 with the first
> line's stock untouched. The three tests are Task 8's #1, #5 and the multi-line variant of #1.
