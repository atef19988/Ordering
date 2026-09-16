# Concurrent Order Processing

An order service where **stock can never go negative**, **submissions are idempotent**,
**cancellation restores stock exactly once**, and **order-created notifications are delivered
through a transactional outbox → RabbitMQ → consumer with bounded retries** — plus a small
Angular console to drive it.

| Layer | Choice |
|---|---|
| API | ASP.NET Core 9, Minimal APIs, ProblemDetails, output caching, Server-Sent Events |
| Write / read | EF Core 9 (writes, one transaction per command) / Dapper (reads, raw SQL) |
| Database | SQL Server 2022 — the only source of truth |
| Messaging | Outbox table → relay → RabbitMQ 4 → notification consumer |
| Hot state | Redis 7 — shared catalogue cache only, never the truth |
| Tests | xUnit + Testcontainers (SQL Server, RabbitMQ, Redis) + WebApplicationFactory |
| Frontend | Angular 21, standalone components, signals, `EventSource` |

## Prerequisites

- Docker Desktop (SQL Server, RabbitMQ and Redis run in containers; the tests start their own).
- .NET SDK 9.0.300+ (`global.json` rolls forward to a newer major).
- Node 20+ only if you want to run the Angular console outside Docker.

## Run it

**Fastest — everything in Docker:**

```bash
docker compose --profile app up -d --build
```

| What | Where |
|---|---|
| Order console | http://localhost:8080 |
| API + Swagger | http://localhost:5000/swagger — health at `/health` |
| RabbitMQ management UI | http://localhost:15672 (`guest` / `guest`) |

The API migrates the database and seeds the two products (`SKU-001` 12.50 × 10, `SKU-002`
4.00 × 40) on first start. Stop everything with `docker compose --profile app down`.

**For development — infrastructure in Docker, code on the host:**

```bash
docker compose up -d                                     # SQL Server :1433, RabbitMQ :5672, Redis :6379
dotnet run --project src/Ordering.Api                     # https://localhost:5001 / http://localhost:5000, migrates + seeds (Development)
cd web/order-console && npm ci && npm start               # http://localhost:4200, /api proxied to :5000
```

Optional: `dotnet run --project src/Ordering.Api -- seed --products 100000` fills a large
catalogue (`LOAD-000001…`) to exercise the paged product list.

### Try the API

```bash
# create (Idempotency-Key is required; GUID or ULID)
curl -s -X POST http://localhost:5000/api/orders \
  -H "Content-Type: application/json" -H "Idempotency-Key: 9f1c2a7e-0000-4000-8000-000000000001" \
  -d '{"customerReference":"CUST-42","lines":[{"productCode":"SKU-001","quantity":2}]}'

curl -s http://localhost:5000/api/orders/{id}              # detail + notificationStatus/notificationAttempts
curl -s -N http://localhost:5000/api/orders/{id}/events    # SSE: full order on connect and on every change
curl -s -X POST http://localhost:5000/api/orders/{id}/cancel
curl -s "http://localhost:5000/api/products?search=SKU&sort=name&pageSize=50"   # keyset-paged
```

Notification delivery is simulated. Make it fail to watch the retries:

```bash
Notifications__Delivery__FailureMode=FailFirstN dotnet run --project src/Ordering.Api   # None | AlwaysFail | FailFirstN | Random
```

With `FailFirstN` (N = 2) the order's `notificationStatus` goes `Pending → Sent` with
`notificationAttempts: 3`; with `AlwaysFail` it ends `Failed` after `MaxAttempts` (5).

## Run the tests

```bash
dotnet build      # warnings as errors
dotnet test       # ~1 minute; needs Docker. Pulls mssql/server:2022-latest, rabbitmq:4-management, redis:7-alpine on first run
cd web/order-console && npm run lint && npm run build
```

90 tests: pure domain/pricing rules in `tests/Ordering.UnitTests`, everything else against a
**real** SQL Server, RabbitMQ and Redis in `tests/Ordering.IntegrationTests` (no EF in-memory
anywhere). Concurrent requests are released together from a barrier, and invariants are read
back from the database with Dapper, not from the HTTP response.

| Required behaviour | Test |
|---|---|
| Two orders race for the last unit → one 201, one 409, stock = 0 (also a 50-way race) | `CreateOrderTests` |
| 20 concurrent submits with one key → one order, one deduction, one outbox row, every response the same id | `IdempotencyTests` |
| Same key, changed quantity → `409 idempotency.key_reuse`, nothing written | `IdempotencyTests` |
| Two concurrent cancels → stock back to its pre-order value exactly once | `CancelOrderTests` |
| Process fails after the stock update, before commit → no order, no outbox row, stock unchanged (real rollback, SQL log shows `UPDATE products` then `ROLLBACK`) | `CreateOrderTests` |
| Delivery fails twice then succeeds → `Sent`, 3 attempts; always fails → `Failed`, retries stop, queues empty | `NotificationDeliveryTests` |
| Consumer process dies after the send but **before the ACK** → broker redelivers to a second host | `NotificationDeliveryTests` (see "At-least-once" below) |

Each safeguard was also removed on purpose to confirm its test goes red (details in
`docs/assumptions.md`, Task 8).

## How it works

- **One transaction per order.** `TransactionBehavior` opens it; the handler inserts the order,
  its lines, the outbox message and the idempotency key in one batch, then runs the stock
  decrement **last**, so a hot product row is locked for one round trip:
  `UPDATE products SET available_quantity -= @qty WHERE code = @code AND available_quantity >= @qty`.
  Zero rows affected → 409, everything rolled back. `CHECK (available_quantity >= 0)` is the backstop.
- **Idempotency is a primary key**, not a read-then-write. A duplicate blocks on the key row until
  the first transaction ends, then reads the committed order: same payload → `200` with the
  original order (in its *current* state, even if cancelled since); different payload → `409`.
- **Cancel is a guarded update**: `UPDATE orders SET status='Cancelled' WHERE id=@id AND status='Confirmed'`.
  Exactly one of N racing cancels affects a row, and only that one restores stock. The others answer 200.
- **No process-local locks anywhere** — the database is correct across N API instances.
- **Notifications**: the outbox row is written in the order's transaction; a relay claims rows
  (`UPDLOCK, READPAST`, lease) and publishes with confirms; the consumer counts the attempt and
  delivers with no connection open; failures go to per-attempt retry queues with exponential
  TTL (200, 400, 800, 1600 ms), then `Failed`. Nothing lives in memory, so restarts lose nothing.
- **At-least-once, stated and tested.** The event id (`outbox_messages.id`, a Snowflake) is the
  AMQP `MessageId` and never changes across retries. A consumer that dies *after writing `Sent`
  and before the ACK* is redelivered and acknowledged without a second send (the row's status is
  the dedupe). A consumer that dies *after the provider accepted the send and before the verdict
  is written* **does** send again — the same event id both times, `attempt_count = 2` — which is
  the window only the downstream provider can close by deduplicating on that id. Both cases are
  tests that kill a real host and let RabbitMQ redeliver to a second one.
- **Reads**: Dapper on their own connections; `GET /api/products` is keyset-paged
  (`search` prefix, `inStock`, `sort` from a whitelist, `pageSize ≤ 200`, opaque cursor) and
  served from a Redis-backed output cache evicted after every create/cancel commit (Redis down →
  plain database read, `/health` degraded, never an error). `GET /api/orders/{id}/events` pushes
  the full order over SSE on every change hint (a RabbitMQ fan-out), so the console never polls.
- **Every id is a Snowflake `bigint`** generated before insert; it exceeds 2^53, so ids are JSON
  strings on the wire.

More: `docs/architecture.md` (design decisions), `docs/capacity.md` (measured numbers),
`docs/assumptions.md` (the full per-task build log), `docs/tasks/` (the build plan).

## Project layout

```
src/Ordering.Domain          entities, value objects, domain errors — no dependencies
src/Ordering.Application     commands/queries (one vertical slice per file), abstractions, pipeline behaviours
src/Ordering.Infrastructure  EF Core, Dapper, outbox relay, RabbitMQ, Redis, consumer, fake delivery
src/Ordering.Api             minimal-API endpoints, DI, middleware, SSE, health
tests/                       Ordering.UnitTests, Ordering.IntegrationTests (Testcontainers)
web/order-console            Angular console
docs/                        architecture, capacity, assumptions, task specs
```

Dependency rule: `Api → Infrastructure → Application → Domain`, never the reverse (asserted by a test).

## Assumptions and decisions

- **Stack deviations from the brief were owner instructions:** Minimal APIs instead of controllers,
  SQL Server instead of PostgreSQL, Snowflake ids instead of GUIDs, RabbitMQ + Redis added for
  load. All specs and docs are written for this stack.
- **No auth, no deployment, no real payment provider, no sharding, no 202/asynchronous intake.**
  `POST /api/orders` answers 201/409 synchronously because the brief's tests require it.
- **Money is `decimal(18,2)`**; line totals are rounded half away from zero; totals are always
  computed from catalogue prices — a price in the request is ignored.
- **Duplicate product codes in one request are rejected (400)**, not merged.
- **`Idempotency-Key` must be a GUID or ULID** (400 otherwise). Payload equivalence is a SHA-256 of
  a canonical form (trimmed reference, lines sorted by code), so key order and whitespace do not
  matter.
- **Lock waits are bounded** (`SET LOCK_TIMEOUT 3000`): a busy product row answers
  `503 stock.busy` with `Retry-After: 1`, safe to retry with the same key; a duplicate still inside
  its transaction answers `409 idempotency.in_progress`. Neither commits anything.
- **Cancelling does not withdraw the notification**: the order really was created, so
  `order.created` is still delivered.
- **Each running API instance needs a distinct `Snowflake:WorkerId` (0–1023)** — there is no
  database coordination for id uniqueness. Development ships `0`.
- **Retry back-off has no jitter**: one RabbitMQ queue per attempt (RabbitMQ only expires from the
  head of a queue), so tiers are fixed per attempt. Changing `MaxAttempts`/back-off needs the old
  tier queues deleted (development only).
- **RabbitMQ `guest`/`guest` and the SQL `sa` password in `appsettings.json` are development-only.**
- **The console is same-origin** through the dev proxy / nginx, so no CORS is configured.
- **Angular 21 (spec said 18+)** — the CLI on the build machine; zoneless, signals, `OnPush`.

## Not built (optional scaling work, does not affect the requirements above)

- Task 12 (`READ_COMMITTED_SNAPSHOT`, lock-free pre-check, one-round-trip deduct) and Task 14
  (Redis stock admission gate, idempotency fast path, concurrency limits). Reads can still queue
  behind a writer's row lock on a hot product; the output cache hides most of it.
- The one-page design note (Task 11). `docs/architecture.md` holds the design decisions.
- The Angular feature (Task 10) and the read-path work (Task 13) were verified by driving the app
  and the API by hand; their automated Definition-of-done boxes are unticked in `docs/tasks/`.

## AI disclosure

Built with Claude Code as a pair programmer, task by task against `CLAUDE.md` and the specs in
`docs/tasks/`; every task ended with `dotnet build` + `dotnet test` green and was reviewed before
the next started.
