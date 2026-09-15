# Architecture decisions

Database is **SQL Server 2022**; every statement below is T-SQL. Identifiers are 64-bit
**Snowflake** ids (`IIdGenerator.NewId()`, `bigint` in the database, JSON strings on the wire).
Three systems, three roles: **SQL Server is the truth, RabbitMQ moves messages, Redis holds hot
state.** Nothing in RabbitMQ or Redis is ever needed to decide whether an order is valid.

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

One transaction per command, opened by `TransactionBehavior`, committed only if the handler
returns a success `Result`. Nothing outside a handler opens a transaction. No I/O to an external
system happens inside a transaction — that is what the outbox is for. I/O that must *follow* a
commit (cache eviction, change hints, gate release) is registered with `IUnitOfWork.OnCommitted`
and run by `TransactionBehavior` after the commit, best-effort.

`CreateOrder` transaction contains exactly, in this order (Task 4 built 1→3→2→4; Task 5
reorders):

1. `SET LOCK_TIMEOUT 3000`,
2. catalogue read, and a lock-free pre-check of `available_quantity` (Task 12) that answers the
   hopeless 409s before anything is written — a pre-check, not a guard,
3. one `SaveChanges`: insert `orders` + `order_lines`, `outbox_messages` (`order.created`),
   `idempotency_keys`,
4. conditional stock decrement per line, one round trip, **last**,
5. commit.

Any failure → rollback → zero side effects. Putting the stock update last means the exclusive
lock on a product row is held for one round trip plus the commit's log flush, and a duplicate
key conflicts on `pk_idempotency_keys` before it ever touches a product row.

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

Isolation level: `READ COMMITTED` with `READ_COMMITTED_SNAPSHOT ON` (Task 12). Readers see the
last committed version and never block behind a writer's row lock, which is what keeps
`GET /api/products` fast during a hot run. The guard is unaffected: an `UPDATE` locates rows
through the version store but takes its `U`/`X` lock and re-evaluates the `WHERE` against the
**current** row, so racing decrements still serialise. We do not need `SERIALIZABLE`, and we do
not retry deadlock victims (error 1205) beyond mapping them to a 409 — multi-line orders always
touch products in product-code order (in one batched round trip, Task 12), which prevents the
create/create and create/cancel deadlocks in the first place.

Lock waits are bounded: `SET LOCK_TIMEOUT 3000` on every write; a timeout on a product row is
`503 stock.busy` with `Retry-After: 1`, safe to retry with the same key.

The ceiling this leaves is **orders/s per product ≈ 1000 ÷ lock-hold-ms** on a hot product, and
the commit (log flush) rate across products. `docs/capacity.md` records both as measured.

## 3. Database constraints that carry correctness

| Constraint | Protects |
|---|---|
| `CHECK (products.available_quantity >= 0)` | stock never negative |
| `PRIMARY KEY (idempotency_keys.idempotency_key)` | one order per key |
| `CHECK (order_lines.quantity > 0)` | no zero/negative line items |
| `PRIMARY KEY (outbox_messages.id)` — a Snowflake id set before insert | stable event id across retries |
| filtered `UNIQUE (outbox_messages.type, aggregate_id) WHERE type = 'order.created'` | one created-notification per order |
| FK `order_lines.order_id -> orders.id ON DELETE CASCADE` | no orphan lines |
| `UPDATE … WHERE status = 'Processing'` on every consumer write | a terminal outbox row is never reopened by a duplicate delivery |

## 4. Idempotency lifecycle

The key row is inserted **inside** the order transaction, in the same `SaveChanges` as the order
and before the stock update. It therefore exists only if the order committed, which removes any
"stuck in-progress" state to clean up.

```
POST /api/orders with Idempotency-Key: K
  ├─ Redis idem:K present? (Task 14, outside any transaction)
  │    ├─ hash == H → 200 OK, current order (replay, no SQL write)
  │    └─ hash != H → 409 idempotency.key_reuse (no SQL)
  ├─ begin tx, SET LOCK_TIMEOUT 3000
  ├─ insert order, lines, outbox, idempotency_keys(K, H, O)   (one SaveChanges)
  │    ├─ SqlException 2627 on pk_idempotency_keys → rollback → read committed row R
  │    │     ├─ R.request_hash == H → 200 OK, original order (replay)
  │    │     └─ R.request_hash != H → 409 idempotency.key_reuse
  │    └─ SqlException 1222 (lock timeout on the key) → 409 idempotency.in_progress, Retry-After: 1
  ├─ deduct stock (conditional UPDATE, last)
  │    ├─ 0 rows → rollback (key row included) → 409 stock.insufficient
  │    └─ 1222 → rollback → 503 stock.busy, Retry-After: 1
  ├─ commit → 201 Created
  └─ after commit: Redis idem:K = H:O (24 h)
```

**Payload equivalence.** `request_hash` = SHA-256 of a canonical form of the body:
customer reference trimmed, line items sorted by product code, quantities as integers,
no whitespace, no other headers. Same goods, same customer, any key order or formatting → same
hash. A changed quantity, product or customer → different hash → 409.

**Duplicate still processing.** The second insert of key `K` blocks on the primary-key lock until
the first transaction ends. Normally it then fails with 2627, rolls back, reads the committed row
and returns `200 OK` with the original order — the caller never sees a partial state, and never
waits on a product row. `SET LOCK_TIMEOUT 3000` bounds the wait; on timeout the API returns
`409 idempotency.in_progress` with `Retry-After: 1`, which is safe to retry with the same key.

**Replay after cancellation.** Replaying key `K` returns the stored order *in its current state*,
i.e. `Cancelled`. It never re-creates or re-confirms it, because the key row still exists and the
handler never runs again. The Redis entry is only ever written after a commit, so a Redis hit
describes a committed order and the replay it serves is the same one the database would serve.

## 5. Cancellation

```sql
UPDATE orders SET status = 'Cancelled', cancelled_at = SYSDATETIMEOFFSET()
 WHERE id = @id AND status = 'Confirmed';
```

Rows affected `1` → restore stock for each line in the same transaction, in product-code order,
last before commit, and return 200. Rows affected `0` → the order is already `Cancelled` (return
200, idempotent) or does not exist (404). Stock is therefore restored exactly once no matter how
many concurrent cancels arrive. After commit: catalogue cache evicted, change hint published,
gate released (Tasks 13–14).

## 6. Outbox, relay, RabbitMQ, consumers

`outbox_messages(id, type, aggregate_id, payload, occurred_at, status, attempt_count,
next_attempt_at, published_at, claimed_by, claimed_until, processed_at, last_error)`

```
outbox_messages ─claim─▶ OutboxRelay ─publish, confirms─▶ RabbitMQ ─▶ NotificationConsumer ─▶ delivery
        ▲                                                                       │
        └─────────── Sent / Failed / next_attempt_at, guarded by status ◀───────┘
```

**Relay** (N instances). Claims a batch atomically:

```sql
UPDATE m
   SET status = 'Processing',
       claimed_by = @worker,
       claimed_until = DATEADD(second, @lease, SYSDATETIMEOFFSET())
OUTPUT inserted.*
  FROM outbox_messages AS m WITH (UPDLOCK, READPAST, ROWLOCK)
 WHERE m.id IN (SELECT TOP (@batch) id
                  FROM outbox_messages WITH (UPDLOCK, READPAST, ROWLOCK)
                 WHERE status = 'Pending'
                   AND next_attempt_at <= SYSDATETIMEOFFSET()
                 ORDER BY occurred_at);
```

`UPDLOCK, READPAST` (rows locked by another relay are skipped, not waited for) + a lease is what
makes N relays safe. Each row is published to the topic exchange `ordering.events` with routing
key = `type`, `MessageId = id`, persistent, with publisher confirms; on confirm the row gets
`published_at` and the lease is cleared. A full batch loops immediately; only an empty claim
sleeps. A **reaper** returns to `Pending` any `Processing` row whose lease expired before it was
published, or that was published but got no verdict within a timeout — re-publishing is how a
lost message or a dead consumer is recovered.

**Topology.** `notifications.order-created` (durable, bound `order.created`) feeds the
consumers. Failed attempts are re-published to `ordering.retry` with routing key `attempt.{n}`,
which routes to `notifications.retry.a{n}` — one queue per attempt with `x-message-ttl =
min(base × 2^(n−1), cap)` and a dead-letter back to `ordering.events`. One queue per tier because
RabbitMQ expires only from the head of a queue. Unparseable messages go to `notifications.dead`.

**Consumer** (N instances, prefetch). Per message: `UPDATE … SET attempt_count += 1 OUTPUT
inserted.attempt_count WHERE id = @id AND status = 'Processing'` — zero rows means the row is
already terminal, ACK and stop. Then deliver with no database connection open. Success →
`status = 'Sent'` (guarded by `status = 'Processing'`). Failure → `next_attempt_at`,
`last_error`, re-publish to the tier, ACK; or `status = 'Failed'` once `MaxAttempts` is reached.
The order API reports `Pending | Processing → Pending`, `Sent`, `Failed`.

Event id = `outbox_messages.id` = AMQP `MessageId`, stable across retries, tiers and
re-publishes, passed to the delivery service so a consumer can deduplicate.

**At-least-once, every case.** Delivery succeeded but the consumer died before `Sent` was
written → broker redelivers → counted and delivered again. Consumer died between the retry
publish and the ACK → original redelivered → one extra attempt, still bounded. Published but the
verdict never came → reaper re-publishes → duplicate, absorbed by the `Processing` guard if the
verdict did arrive meanwhile. The consumer must treat the event id as a dedupe key. This is
documented, not hidden.

## 7. Notification status in the order API

`GET /api/orders/{id}` reports `notificationStatus` derived from the `order.created` outbox row:
`Pending` (queued, published, or retrying), `Sent`, `Failed` (attempts exhausted), and
`notificationAttempts` = the consumer's count.

## 8. Payment (design only, Task 11)

No provider call inside a transaction. Order commits as `AwaitingPayment` with a
`payment_intents` row and an outbox message. A payment worker calls the provider using the
intent id as the provider's idempotency key, then commits the result in a separate short
transaction. If the provider succeeds and the app dies before that commit, reconciliation
re-queries the provider with the same idempotency key and converges — it never double-charges.

## 9. Read path (Task 13)

`GET /api/products` is served by ASP.NET Core output caching backed by Redis (1 s TTL, tag
`catalogue`), shared by every instance; create and cancel evict the tag after commit, so the
TTL is only the bound on a lost eviction. Redis down = cache miss, never an error.
`GET /api/orders/{id}` is not cached. `GET /api/orders/{id}/events` is a Server-Sent Events
stream that sends the full `OrderDetailDto` on connect and again on every change hint; hints are
`{orderId}` on a non-durable fanout exchange `ordering.hints`, published after cancel commits and
after the consumer writes a verdict, consumed by every API instance into an in-process hub. The
stream carries state, not deltas, so a lost or duplicated hint cannot desynchronise a client.

## 10. Admission (Task 14)

Requests that cannot succeed are answered before a transaction exists, in a pipeline behaviour
that runs ahead of `TransactionBehavior`:

1. **Idempotency fast path.** `idem:{key}` in Redis (written only after a commit) → `200` replay
   or `409 key_reuse` with no SQL write. Miss → §4.
2. **Stock gate.** `stock:{code}` integers in Redis, reserved all-or-nothing by a Lua script;
   a short line → `409 stock.insufficient` (`available: 0`) in microseconds. A missing key passes
   through. The reservation is consumed by the conditional `UPDATE`; on any other outcome it is
   released. A reconciler rewrites every key from the database every 10 s with a TTL, so drift in
   either direction is bounded by one interval and is harmless: too low costs false 409s, too
   high lets a few requests through to the database, which rejects them. **The gate can only say
   no.** Any Redis error or slow call opens the gate.
3. **Concurrency limits.** A concurrency limiter below `Max Pool Size` on writes, a larger one on
   reads, both answering `503` with `Retry-After` when full; a 10 s request timeout on writes.
   The API never lets a request wait on the connection pool.

## 11. Capacity model

Measured, not estimated, in `docs/capacity.md`; the shape is:

| Path | Bounded by | Lever used | Next lever (design only) |
|---|---|---|---|
| Orders on one hot product | row lock hold time | stock update last; one round trip; RCSI; gate rejects the hopeless | counter in Redis with the DB as a reconciled ledger; or K stock buckets per product |
| Orders across products | commit (log flush) rate | fewer round trips; fewer wasted rollbacks | faster log disk; nothing else on one instance |
| Catalogue reads | Redis | shared output cache, 1 s | CDN in front |
| Status reads | connection count | SSE push instead of 2 s polling | — |
| Notifications | consumer count × prefetch | relay never sleeps on a full batch; N consumers | — |
| Rejections during a sell-out | Redis | gate answers without SQL | edge sold-out flag |

Not done, and why: no sharding (one truth, one schema); no Redis-as-ledger (the brief says the
database enforces stock, and the correctness argument in §10 depends on it); no 202 intake
(the brief and the tests require a synchronous 409).
