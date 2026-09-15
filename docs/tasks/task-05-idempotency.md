# Task 5 — Idempotency lifecycle

Read `docs/architecture.md` §1 and §4 first. Implement exactly that design.

This task also **reorders the create transaction** that Task 4 built: everything that needs no
product row lock happens first, the conditional stock update happens last. Two things fall out
of that: a duplicate key conflicts before it ever touches stock, and a hot product row is locked
for one round trip plus the commit instead of the whole handler. Task 12 measures the difference.

## Table

```sql
CREATE TABLE idempotency_keys (
    idempotency_key nvarchar(128)  NOT NULL CONSTRAINT pk_idempotency_keys PRIMARY KEY,
    request_hash    char(64)       NOT NULL,
    order_id        bigint         NOT NULL CONSTRAINT fk_idempotency_keys_orders REFERENCES orders(id),
    created_at      datetimeoffset NOT NULL
);
```

(`key` is a reserved word in T-SQL; the column is `idempotency_key` everywhere.)

## Entity and store

The key row is written by EF in the **same `SaveChanges`** as the order, so the FK to `orders`
is satisfied inside one batch and a primary-key violation surfaces as one `DbUpdateException`.

```
Application/Idempotency/IdempotencyKey.cs        entity: Key, RequestHash, OrderId, CreatedAt (all set in the ctor, no setters)
Application/Idempotency/IIdempotencyStore.cs     void Add(IdempotencyKey key);
                                                 Task<IdempotencyKey?> FindAsync(string key, CancellationToken ct);   // new connection, read-only
Application/Idempotency/RequestFingerprint.cs    Compute(CreateOrderCommand) → SHA-256 hex
Infrastructure/Persistence/IdempotencyStore.cs   Add = context.Add; FindAsync = Dapper on IDbConnectionFactory
Infrastructure/Persistence/Configurations/IdempotencyKeyConfiguration.cs
```

`FindAsync` runs on its own connection because the handler calls it *after* rolling back — the
EF connection's transaction is gone by then, and a read on a second connection would otherwise
have blocked on our own uncommitted rows.

## Translating SQL errors — Infrastructure only

`Application` never sees `SqlException`. Infrastructure translates two error numbers into typed
exceptions declared in `Application/Abstractions/Persistence/`:

| SQL Server | Thrown as | Where |
|---|---|---|
| 2627 / 2601 (unique violation) | `UniqueViolationException(string ConstraintName)` — name parsed from the message | `UnitOfWork.SaveChangesAsync` |
| 1222 (lock request time-out) | `LockTimeoutException(string Resource)` — the *call site* names the resource: `"idempotency_keys"` or `"products"` | `UnitOfWork.SaveChangesAsync`, `StockRepository.TryDeductAsync` |

Check `Number` and the constraint name; never a free-text message match alone. Anything else
propagates as today (500 via `ExceptionHandlingMiddleware`).

## Hashing — `Application/Idempotency/RequestFingerprint.cs`

`Compute(CreateOrderCommand)` → SHA-256 hex of the canonical form:

```
customerReference.Trim()          (case-sensitive; document this)
+ '|' + lines ordered by productCode, each "CODE:QTY"
```

Product codes are normalised the way Task 4 normalises them (trimmed, catalogue casing is not
known yet at hash time, so upper-case them — say so). No whitespace, no JSON key order, no
headers involved. Document this definition verbatim in the README under "Payload equivalence".

## Result shape

The command now returns `CreateOrderResponse(OrderDetailDto Order, bool Replayed)`. The endpoint
maps `Replayed ? 200 OK : 201 Created` — that is the only branch it is allowed to have, and it is
on a result flag, not a business rule. Task 14's admission behaviour reads the same flag.

Two additions to the single Result→HTTP mapping in `ResultExtensions`:
- `ErrorType.Unavailable` → `503`.
- `Error.Details["retryAfterSeconds"]` (int) → `Retry-After` header, on any status.

## Flow in `CreateOrderCommand` handler

1. `await UnitOfWork.SetLockTimeoutAsync(TimeSpan.FromSeconds(3), ct)` — issues
   `SET LOCK_TIMEOUT 3000;` on the transaction's connection. It is connection-scoped in SQL
   Server, so it runs inside the transaction every time.
2. Catalogue prices in code order, unknown code → 404 (Task 4, unchanged).
3. Build the `Order`; stage order + lines + outbox row (Task 4) **and**
   `IIdempotencyStore.Add(new IdempotencyKey(key, RequestFingerprint.Compute(command), order.Id, Clock.UtcNow))`.
4. `await UnitOfWork.SaveChangesAsync(ct)` — one batch: `orders`, `order_lines`,
   `outbox_messages`, `idempotency_keys`. The handler calls this itself; the base class's final
   save becomes a no-op (nothing left to flush). Say so in the README.
   - `UniqueViolationException { ConstraintName: "pk_idempotency_keys" }` → `RollbackAsync`,
     then `IIdempotencyStore.FindAsync(key)`:
     - `RequestHash` matches → load the order through the Task 3 query →
       `Replayed: true` → `200 OK`.
     - `RequestHash` differs → `409 idempotency.key_reuse`.
   - `LockTimeoutException { Resource: "idempotency_keys" }` → `409 idempotency.in_progress`,
     `retryAfterSeconds: 1`.
5. Conditional stock decrement per line, in catalogue code order (`IStockRepository.TryDeductAsync`,
   unchanged). Zero rows → `409 stock.insufficient`; the whole transaction rolls back —
   **including the key row**, so a stock conflict does not consume the key and the same key may
   be retried once stock exists.
   - `LockTimeoutException { Resource: "products" }` → `503 stock.busy`, `retryAfterSeconds: 1`.
     Nothing was committed; the client retries with the same key.
6. Return success → `TransactionBehavior` commits → `201 Created`.

`IUnitOfWork` gains `SetLockTimeoutAsync(TimeSpan, CancellationToken)`; the SQL stays in
Infrastructure.

## Why this shape

The key row commits with the order, so there is no in-progress state to clean up after a crash,
and a duplicate that arrives mid-flight simply waits for the primary-key lock and then replays.
Do not replace it with a read-then-insert "check if key exists" — that is a race.

Writing the key row *before* the stock update means the second of two racing duplicates blocks on
`pk_idempotency_keys` while the first is still inside the transaction, and never reaches the
product row. Concurrent duplicates therefore cost the hot product nothing. And because the stock
update is the last statement, the exclusive lock on `products` is held for exactly one round
trip plus the commit's log flush — the shortest window this design allows.

A 409 for stock now rolls back four inserts instead of zero. That is accepted: stock conflicts
are the minority path, and Task 12 adds a lock-free pre-check that answers the obvious ones
before anything is written.

## Replay semantics

- Replay after cancellation returns the order with `status: "Cancelled"`. It never re-creates or
  re-confirms. Cover this with a test.
- The response to a replay is the **current** state of the order, not a snapshot of the original
  response. Say so in the README.

## Definition of done

- [ ] Same key + same payload (different JSON key order / extra whitespace) → 200, same order id, stock deducted once
- [ ] Same key + changed quantity → 409 `idempotency.key_reuse`, zero new rows in orders/order_lines/outbox
- [ ] 20 concurrent requests, same key, same payload → exactly one order, one deduction, one `order.created` outbox row
- [ ] Missing `Idempotency-Key` → 400
- [ ] Cancel then replay → 200 with `Cancelled`, stock not deducted again
- [ ] Stock conflict does not consume the key: 409, then restock, then the same key → 201
- [ ] A test connection holding an exclusive lock on the product row → `503 stock.busy` with `Retry-After: 1` after ~3 s, and no order row
- [ ] SQL log (or EF command interceptor) shows the `UPDATE products` as the last statement before `COMMIT`
