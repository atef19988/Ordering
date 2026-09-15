# Task 14 — Admission control: Redis gate, fast replays, early 503s

A flash sale is mostly requests that cannot succeed: a product with 100 units gets a million
attempts. Today each of them opens a transaction, and once the row lock queue passes 3 s they
fail with lock timeouts or pool timeouts. This task answers the hopeless ones in Redis in
microseconds, replays known keys without a write transaction, and turns "full" into a fast 503
with `Retry-After` instead of a slow failure.

**Read `CLAUDE.md` rule 9 before writing a line.** Redis may produce a 409 or a 200 replay on
its own. It may never produce a 201. Every success still runs the conditional `UPDATE` and the
unique-index insert in SQL Server; Redis is an *admission gate*, not a stock ledger.

## 1. Rate limiting — `Api/Admission/`

```csharp
builder.Services.AddRateLimiter(o =>
{
    o.AddConcurrencyLimiter("orders-write", p => { p.PermitLimit = Admission.WriteConcurrency; p.QueueLimit = Admission.WriteQueue; p.QueueProcessingOrder = OldestFirst; });
    o.AddConcurrencyLimiter("reads",        p => { p.PermitLimit = Admission.ReadConcurrency;  p.QueueLimit = 0; });
    o.OnRejected = /* ProblemDetails 503, code server.busy, Retry-After: 1, through the same ProblemDetails writer the middleware uses */;
});
```

Defaults: `WriteConcurrency 64`, `WriteQueue 256`, `ReadConcurrency 512`. `orders-write` on
`POST /api/orders` and `POST /api/orders/{id}/cancel`; `reads` on every `GET`. The write limit
stays **below** `Max Pool Size` so a request never waits 15 s for a connection — that is the
whole point. 503 rather than 429: the server is full, not the client over quota.

`AddRequestTimeouts`: policy `write` = 10 s on the two POSTs → `503 server.timeout`. The
cancellation token rolls the transaction back; the client retries with the same key.

## 2. Stock gate — `IStockGate`

```csharp
// Application/Abstractions/Admission/IStockGate.cs
Task<GateDecision> TryReserveAsync(IReadOnlyList<StockLine> lines, CancellationToken ct);  // Reserved | Rejected(code) | Open
Task ReleaseAsync(IReadOnlyList<StockLine> lines, CancellationToken ct);
```

`Infrastructure/Redis/RedisStockGate`: one integer per product at `stock:{CODE}` (code
upper-cased and trimmed, the same normalisation Task 4 applies). Two Lua scripts, so each call
is atomic across all lines of an order:

```lua
-- reserve: KEYS = stock:{code}..., ARGV = qty...   all-or-nothing
for i = 1, #KEYS do
  local s = redis.call('GET', KEYS[i])
  if s and tonumber(s) < tonumber(ARGV[i]) then return i end      -- 1-based index of the short line
end
for i = 1, #KEYS do
  if redis.call('EXISTS', KEYS[i]) == 1 then redis.call('DECRBY', KEYS[i], ARGV[i]) end
end
return 0
```

```lua
-- release: same KEYS/ARGV
for i = 1, #KEYS do
  if redis.call('EXISTS', KEYS[i]) == 1 then redis.call('INCRBY', KEYS[i], ARGV[i]) end
end
```

A key that does not exist is a pass-through: the database decides, exactly as today.

**Fail-open.** Any Redis exception or a call slower than `Stock:Gate:TimeoutMs` (20) returns
`Open`, logs once a minute, and skips Redis for `Stock:Gate:SkipSeconds` (5) on that instance.
Redis down is slower, never wrong.

**Reconciler** — `Infrastructure/Redis/StockGateReconciler : BackgroundService`, every
`Stock:Gate:ReconcileSeconds` (10): `SELECT code, available_quantity FROM products` (Dapper) and
`SET stock:{CODE} <value> EX <3 × ReconcileSeconds>`. The TTL means a dead reconciler empties the
gate and it falls open. Overwriting in-flight reservations is accepted: drift in either direction
is bounded by one interval and is harmless — Redis too low costs false 409s until the next
reconcile; Redis too high lets a few extra requests reach the database, where the conditional
`UPDATE` rejects them and the release script restores the count. Write this paragraph in the
README as the correctness argument.

## 3. Idempotency fast path — `IIdempotencyCache`

```csharp
// Application/Abstractions/Admission/IIdempotencyCache.cs
Task<IdempotencyEntry?> FindAsync(string key, CancellationToken ct);              // (RequestHash, OrderId)
Task SetAsync(string key, IdempotencyEntry entry, TimeSpan ttl, CancellationToken ct);
```

`Infrastructure/Redis/RedisIdempotencyCache`: `idem:{key}` → `"{hash}:{orderId}"`, TTL 24 h,
written only after a successful commit. A hit therefore always describes a committed order:
same hash → load through the Task 3 query → `200` replay without a write transaction; different
hash → `409 idempotency.key_reuse` without touching SQL Server. A miss falls through to Task 5's
path, where the unique index still enforces the rule. Redis errors are a miss.

## 4. Where it runs — `Features/Orders/CreateOrder/CreateOrderAdmission.cs`

A closed pipeline behaviour `IPipelineBehavior<CreateOrderCommand, Result<CreateOrderResponse>>`,
registered explicitly **before** the open-generic `TransactionBehavior`, so nothing here happens
inside a transaction (`CLAUDE.md` rule 10). In order:

1. Validation has already run (the `ValidationBehavior` sits first).
2. `IIdempotencyCache.FindAsync(command.IdempotencyKey)` → hit: replay or `409` as above; return.
3. `IStockGate.TryReserveAsync(lines)` → `Rejected(code)` → `409 stock.insufficient` with
   `available: 0` from Redis (the gate does not know the exact number; say so in the README).
   Do not read the database to improve the number — that is the round trip being saved.
4. `next()` — Task 5's transaction.
5. Result success with `Replayed: false` → the reservation was consumed by the `UPDATE`; also
   `IIdempotencyCache.SetAsync(key, (hash, order.Id), 24 h)`.
   Any other outcome (failure result, exception, or `Replayed: true` because the key was found in
   the database but not in Redis) → `IStockGate.ReleaseAsync(lines)` in a `finally`.

`CancelOrderCommandHandler` fills its `// Task 14:` seam: `OnCommitted` → `ReleaseAsync` for the
restored lines (a restore is a release from the gate's point of view).

## 5. Metrics — `Meter("Ordering")` in `Infrastructure/Telemetry/`

Counters (tags in braces): `ordering.admission.rejected{reason=server.busy|server.timeout|sse.full}`,
`ordering.gate.decisions{result=reserved|rejected|open}`, `ordering.idempotency.replays{source=redis|database}`,
`ordering.orders.created`, `ordering.stock.conflicts{source=gate|precheck|update}`.
Tests observe them with a `MeterListener`; Task 11 lists them in the design note.

## Not allowed

- Any Redis call inside a transaction, or from a handler other than through `OnCommitted`.
- A code path that returns 201 without the conditional `UPDATE` having affected a row.
- Trusting the gate's count for anything a user can see other than the 409 itself.
- Per-instance memory as a substitute for the gate (rule 2). The behaviour's "skip Redis for
  5 s" timestamp is the only process-local state, and it decides nothing about stock.

## Definition of done

- [ ] `hot` scenario, product stocked with 100 units, 10,000 requests: exactly 100 orders; `UPDATE products` executions in SQL Server ≤ 100 + 20 (reconcile drift); `ordering.gate.decisions{rejected}` ≥ 9,800; rejected requests p99 < 10 ms
- [ ] Redis container paused mid-run → every request reaches the database, `/race "last unit of SKU-001"` holds 5/5, no 500s, `gate.decisions{open}` climbs
- [ ] Reconciler: `SET stock:LOAD-0001 0` by hand while the database has 5 → orders fail for at most `ReconcileSeconds`, then succeed
- [ ] Cancel releases the gate: stock 3, order 3 → gate 0 → cancel → gate 3 → new order 201
- [ ] Replay of a known key: SQL log shows no `BEGIN TRANSACTION` and no `INSERT`; `409 idempotency.key_reuse` likewise served without SQL
- [ ] 300 concurrent POSTs against `WriteConcurrency 64 / WriteQueue 100`: some `503 server.busy` with `Retry-After: 1`, no request waits > 2 s, zero 500s, zero pool-timeout exceptions
- [ ] A write that takes longer than 10 s (hold a lock from a test connection) → `503 server.timeout`, transaction rolled back, no order row
- [ ] `docs/capacity.md` gains the admission table: requests/s the API answers during a sold-out `hot` run, before and after the gate
