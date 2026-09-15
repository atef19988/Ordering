# Task 5 — Idempotency lifecycle

Read `docs/architecture.md` §4 first. Implement exactly that design.

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

## Hashing — `Application/Idempotency/RequestFingerprint.cs`

`Compute(CreateOrderCommand)` → SHA-256 hex of the canonical form:

```
customerReference.Trim()          (case-sensitive; document this)
+ '|' + lines ordered by productCode, each "CODE:QTY"
```

No whitespace, no JSON key order, no headers involved. Document this definition verbatim in the
README under "Payload equivalence".

## Flow in `CreateOrderCommand` handler

1. Run `SET LOCK_TIMEOUT 3000;` on the transaction's connection at the start (it is
   connection-scoped in SQL Server, so issue it inside the transaction every time).
2. Do the work from Task 4.
3. `INSERT INTO idempotency_keys(idempotency_key, request_hash, order_id, created_at) VALUES (...)`.
4. On `SqlException` with `Number` `2627` (primary-key violation) whose message names
   `pk_idempotency_keys` → roll back, then on a **new** connection read the existing row:
   - `request_hash` matches → load the order via the Task 3 query → `200 OK` (not 201).
   - `request_hash` differs → `409` with code `idempotency.key_reuse`.
5. On `SqlException` with `Number` `1222` (lock request time-out) → `409` with code
   `idempotency.in_progress` and header `Retry-After: 1`.

Catch the violation as narrowly as possible — check `Number` and the constraint name, never a
free-text message match alone. A duplicate that arrives while the first transaction is still open
blocks on the primary-key lock (SQL Server holds the key lock until commit), so the second caller
sees either the committed row or the timeout — never a half-written order.

## Why this shape

The key row commits with the order, so there is no in-progress state to clean up after a crash,
and a duplicate that arrives mid-flight simply waits for the row lock and then replays. Do not
replace it with a read-then-insert "check if key exists" — that is a race.

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
