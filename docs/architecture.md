# Architecture decisions

Database is **SQL Server 2022**; every statement below is T-SQL. Identifiers are 64-bit
**Snowflake** ids (`IIdGenerator.NewId()`, `bigint` in the database, JSON strings on the wire).

## 0. Identifiers

`SnowflakeIdGenerator` (Infrastructure) issues ids as `41-bit ms timestamp | 10-bit worker id |
12-bit sequence`. They are generated in the application before insert, so an outbox event id is
known — and stable — the moment the row is written; they are ascending, so `bigint` clustered
primary keys append instead of fragmenting. Generation is lock-free (one CAS over a packed
timestamp/sequence word); a millisecond that runs out of sequence numbers borrows the next one and
a clock that steps backwards is ignored in favour of the logical clock, so ids never repeat or
go backwards within a worker. Uniqueness across instances needs a distinct `Snowflake:WorkerId`
(0–1023) per process. Values exceed JavaScript's 2^53, so the API serialises every `long` as a
string; the Angular models keep ids as `string`.

## 1. Transaction boundary

One transaction per command, opened by `TransactionBehavior` / `BaseCommandHandler`, committed
only if the handler returns a success `Result`. Nothing outside a handler opens a transaction.
No I/O to an external system happens inside a transaction — that is what the outbox is for.

`CreateOrder` transaction contains exactly:
1. conditional stock decrement per line,
2. insert `orders` + `order_lines`,
3. insert `idempotency_keys` row,
4. insert `outbox_messages` row (`order.created`).

Any failure → rollback → zero side effects.

## 2. Concurrency mechanism

Stock is protected by the database, not the application:

```sql
UPDATE products
   SET available_quantity = available_quantity - @quantity
 WHERE code = @code
   AND available_quantity >= @quantity;
```

Rows affected `0` → `Error.Conflict("stock.insufficient")` → HTTP 409, transaction rolled back.
The `UPDATE` takes an exclusive row lock; a second writer on the same product blocks until the
first commits, then re-evaluates its `WHERE` against the new value, so two racing orders for the
last unit can never both succeed. A backstop `CHECK (available_quantity >= 0)` turns any future
mistake into a failed transaction rather than negative stock.

Isolation level: `READ COMMITTED` (SQL Server default) is enough because every write is a
conditional update that re-reads the row under lock. We do not need `SERIALIZABLE`, and we do not
retry deadlock victims (error 1205) beyond mapping them to a 409 — multi-line orders always touch
products in product-code order, which prevents the create/create and create/cancel deadlocks in the
first place.

## 3. Database constraints that carry correctness

| Constraint | Protects |
|---|---|
| `CHECK (products.available_quantity >= 0)` | stock never negative |
| `PRIMARY KEY (idempotency_keys.idempotency_key)` | one order per key |
| `CHECK (order_lines.quantity > 0)` | no zero/negative line items |
| `PRIMARY KEY (outbox_messages.id)` — a Snowflake id set before insert | stable event id across retries |
| filtered `UNIQUE (outbox_messages.type, aggregate_id) WHERE type = 'order.created'` | one created-notification per order |
| FK `order_lines.order_id -> orders.id ON DELETE CASCADE` | no orphan lines |

## 4. Idempotency lifecycle

The key row is inserted **inside** the order transaction. It therefore exists only if the order
committed, which removes any "stuck in-progress" state to clean up.

```
POST /api/orders with Idempotency-Key: K
  ├─ begin tx, SET LOCK_TIMEOUT 3000
  ├─ deduct stock, insert order, insert outbox
  ├─ INSERT idempotency_keys(idempotency_key=K, request_hash=H, order_id=O)
  │    ├─ success                          → commit → 201 Created
  │    └─ SqlException 2627 (PK violation) → rollback → read committed row R
  │         ├─ R.request_hash == H → 200 OK, original order (replay)
  │         └─ R.request_hash != H → 409 idempotency.key_reuse
  └─ end
```

**Payload equivalence.** `request_hash` = SHA-256 of a canonical form of the body:
customer reference trimmed, line items sorted by product code, quantities as integers,
no whitespace, no other headers. Same goods, same customer, any key order or formatting → same
hash. A changed quantity, product or customer → different hash → 409.

**Duplicate still processing.** The second insert of key `K` blocks on the primary-key lock until
the first transaction ends. Normally it then fails with 2627, rolls back, reads the committed row
and returns `200 OK` with the original order — the caller never sees a partial state.
`SET LOCK_TIMEOUT 3000` bounds the wait; on timeout (`SqlException` 1222) the API returns
`409 idempotency.in_progress` with `Retry-After: 1`, which is safe to retry with the same key.

**Replay after cancellation.** Replaying key `K` returns the stored order *in its current state*,
i.e. `Cancelled`. It never re-creates or re-confirms it, because the key row still exists and the
handler never runs again.

## 5. Cancellation

```sql
UPDATE orders SET status = 'Cancelled', cancelled_at = SYSDATETIMEOFFSET()
 WHERE id = @id AND status = 'Confirmed';
```

Rows affected `1` → restore stock for each line in the same transaction and return 200.
Rows affected `0` → the order is already `Cancelled` (return 200, idempotent) or does not exist
(404). Stock is therefore restored exactly once no matter how many concurrent cancels arrive.

## 6. Outbox and the notification worker

`outbox_messages(id, type, aggregate_id, payload, occurred_at, status, attempt_count,
next_attempt_at, claimed_by, claimed_until, processed_at, last_error)`

The worker claims a batch atomically:

```sql
UPDATE m
   SET status = 'Processing',
       claimed_by = @worker,
       claimed_until = DATEADD(second, 30, SYSDATETIMEOFFSET()),
       attempt_count = attempt_count + 1
OUTPUT inserted.*
  FROM outbox_messages AS m WITH (UPDLOCK, READPAST, ROWLOCK)
 WHERE m.id IN (SELECT TOP (@batch) id
                  FROM outbox_messages WITH (UPDLOCK, READPAST, ROWLOCK)
                 WHERE status IN ('Pending', 'Processing')
                   AND next_attempt_at <= SYSDATETIMEOFFSET()
                   AND (claimed_until IS NULL OR claimed_until < SYSDATETIMEOFFSET())
                 ORDER BY occurred_at);
```

`UPDLOCK, READPAST` (rows locked by another worker are skipped, not waited for) + a claim lease is
what makes N workers safe. Backoff is `base * 2^attempt` with jitter, capped; after `MaxAttempts`
the row becomes `Failed` and the order API reports notification status `Failed`.

Event id = `outbox_messages.id`, stable across retries, passed to the delivery service so a
consumer can deduplicate.

**Send-success then crash.** If delivery succeeds but the process dies before `status='Sent'` is
committed, the lease expires and the message is redelivered. Delivery is at-least-once; the
consumer must treat the event id as a dedupe key. This is documented, not hidden.

## 7. Notification status in the order API

`GET /api/orders/{id}` reports `notificationStatus` derived from the `order.created` outbox row:
`Pending` (queued or retrying), `Sent`, `Failed` (attempts exhausted).

## 8. Payment (design only, Task 11)

No provider call inside a transaction. Order commits as `AwaitingPayment` with a
`payment_intents` row and an outbox message. A payment worker calls the provider using the
intent id as the provider's idempotency key, then commits the result in a separate short
transaction. If the provider succeeds and the app dies before that commit, reconciliation
re-queries the provider with the same idempotency key and converges — it never double-charges.
