# Task 13 — Read path: shared cache and pushed status

Two read multipliers dominate a real deployment: every client polling `GET /api/products` for
the stock panel, and every client polling `GET /api/orders/{id}` every two seconds until the
notification flips. This task serves the first from Redis and replaces the second with a push.
The database sees writes, plus one read per actual change.

## Infrastructure

- `docker-compose.yml`: service `redis` from `redis:7-alpine`, port `6379`, healthcheck
  `redis-cli ping`. No persistence configuration — everything Redis holds in this system can be
  rebuilt (cache) or is tolerant of loss (Task 14's gate, which fails open).
- Packages: `StackExchange.Redis`, `Microsoft.AspNetCore.OutputCaching.StackExchangeRedis`,
  `Testcontainers.Redis`.
- Config: `"Redis": { "Configuration": "localhost:6379,abortConnect=false", "InstanceName": "ordering:" }`.
  One `IConnectionMultiplexer` per process (singleton). `/health` gains a Redis check that is
  **degraded**, not unhealthy, when Redis is down — the API keeps serving.
- All Redis code lives in `Infrastructure/Redis/`. `Application` sees only the interfaces below.

## 1. Post-commit hook — `IUnitOfWork.OnCommitted`

```csharp
void OnCommitted(Func<CancellationToken, Task> action);
```

Handlers register actions during the transaction; `TransactionBehavior` runs them **after**
`CommitAsync` returned, in registration order, each wrapped in try/catch and logged at
`Warning` on failure — they never fail the request and never run on rollback. This is the one
sanctioned place for I/O that must follow a commit (cache eviction, change hints, Task 14's gate
release). Nothing else may run I/O after a commit.

## 2. Catalogue from Redis — output cache on `GET /api/products`

```csharp
builder.Services.AddStackExchangeRedisOutputCache(o => { o.Configuration = ...; o.InstanceName = ...; });
builder.Services.AddOutputCache(o => o.AddPolicy("catalogue", p => p
    .Expire(TimeSpan.FromSeconds(1))
    .Tag("catalogue")
    .SetVaryByQuery("search", "inStock", "sort", "pageSize", "cursor")));   // Task 15's paging keys — never rely on the default
products.MapGet("/", ...).CacheOutput("catalogue");
```

- Redis-backed, so every API instance shares one copy and one eviction.
- Varies by every paging key, so page 2 of one search can never be served for page 1 of another
  (Task 15 has the collision test; if Task 15 ran first, this line already exists — keep one).
- Eviction on change: `ICatalogueCache.InvalidateAsync(ct)` (`Application/Abstractions/Caching/`)
  implemented by `IOutputCacheStore.EvictByTagAsync("catalogue")`, registered through
  `OnCommitted` by the create and cancel handlers (fill the `// Task 13:` seams). The TTL is the
  bound on staleness if an eviction is lost; say "≤ 1 s stale" in the README and the UI copy.
- Fail-open: wrap the store in `ResilientOutputCacheStore : IOutputCacheStore` that turns any
  `RedisException`/`RedisConnectionException`/timeout into a miss or a no-op and logs at most once
  per minute. Redis down must mean "uncached", never 500.
- `GET /api/orders/{id}` is **not** cached: it changes on cancel and on delivery, a PK seek is
  cheap, and the stream below removes the polling that made it expensive.

## 3. Change hints — `IChangeHintPublisher`

```csharp
Task OrderChangedAsync(long orderId, CancellationToken ct);   // Application/Abstractions/Messaging/
```

Implemented over a RabbitMQ **fanout** exchange `ordering.hints`: non-durable, non-persistent,
no confirms, fire-and-forget. Each API instance declares an exclusive auto-delete queue bound to
it and a `ChangeHintListener : BackgroundService` feeds an in-process `IOrderChangeHub`
(`Api/Sse/`), which holds a `Channel<long>` per subscribed order id. Nothing here is state that
matters: a lost hint means the client sees the change at its next reconnect or fallback read.

Who publishes:
- `CancelOrderCommandHandler` → `OnCommitted` → hint (fill the `// Task 13:` seam).
- `NotificationConsumer` after writing `Sent` or `Failed` (fill the `// Task 13:` seam in Task 7).
- Create publishes nothing — the caller already holds the 201 body.

## 4. Server-Sent Events — `GET /api/orders/{id}/events`

`text/event-stream`, `Cache-Control: no-cache`, `X-Accel-Buffering: no`. Behaviour:

1. Unknown id → `404` ProblemDetails (the Task 3 query decides).
2. First frame: `event: order`, `data: <OrderDetailDto JSON>` — the current state, always.
3. Subscribe to the hub for the id. On every hint: re-read through the Task 3 query, send
   another `event: order` frame. The stream carries state, never deltas, so a duplicate or lost
   hint cannot desynchronise the client.
4. `: ping` comment every 15 s. Close after `Sse:MaxConnectionSeconds` (300) — `EventSource`
   reconnects and step 2 resends the state, so nothing is lost.
5. `Sse:MaxConnections` per instance (1000): beyond it, `503` with `code: sse.full`,
   `Retry-After: 5`. Counted with an `Interlocked` counter — that is capacity, not correctness.
6. Stop when the order is `Cancelled` **and** its notification is terminal (`Sent`/`Failed`):
   send `event: done` and close. Nothing can change after that.

The endpoint stays thin: it binds the id and calls `OrderEventStream.RunAsync(id, response, ct)`
in `Api/Sse/`; frame writing (`event:`/`data:`/blank line, then flush) lives in one helper.

## What this does not do

- No cache on order detail, no SignalR, no WebSocket, no Redis pub/sub — one broker for
  messages, one cache for reads.
- No hint on create, no delta events, no ordering guarantees between hints.

## Definition of done

- [ ] `GET /api/products` under a `hot` load run is served from Redis: SQL Server shows ≤ 2 catalogue reads per second per instance (`sys.dm_exec_query_stats` or the Dapper log), p99 < 20 ms
- [ ] A create on host A evicts the catalogue: `GET /api/products` on host B reflects the new stock on the very next request (two `OrderingApiFactory` hosts, one Redis)
- [ ] Redis container paused → `GET /api/products` still 200 from the database; `/health` reports Redis degraded; nothing logs more than once a minute
- [ ] SSE client connected to host A, cancel sent to host B → an `event: order` with `Cancelled` arrives within 1 s
- [ ] `FailFirstN: 2` → the stream shows `Pending` then `Sent` with `notificationAttempts: 3`, then `event: done` after a cancel
- [ ] `MaxConnections: 2` → the third stream gets `503 sse.full` with `Retry-After`
- [ ] `docs/capacity.md` gains a "read path" table: products req/s and p99 before/after, and DB reads per second before/after
