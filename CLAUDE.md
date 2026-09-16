# CLAUDE.md — Concurrent Order Processing

Read this file before any task. It is the contract. `docs/tasks/*.md` say *what* to build,
this file says *how* to build it everywhere.

## What we are building

An order service where stock can never go negative, submissions are idempotent, cancellation
restores stock exactly once, and order-created notifications are delivered through a
transactional outbox and RabbitMQ with bounded retries. It is built to absorb load: a short lock
window on the database, reads served from Redis, status pushed instead of polled, a Redis
admission gate in front of hot products, and early 503s when full — every change measured.
Plus a small Angular console to drive it.

## Stack

| Layer | Choice |
|---|---|
| API | ASP.NET Core 9, Minimal APIs, ProblemDetails, output caching, rate limiting, SSE |
| Write model | EF Core 9 + Microsoft.EntityFrameworkCore.SqlServer |
| Read model | Dapper (raw SQL, read-only) |
| Database | SQL Server 2022 (docker compose), `READ_COMMITTED_SNAPSHOT` on — the only source of truth |
| Messaging | Transactional outbox table → relay → RabbitMQ 4 (`RabbitMQ.Client` 7.x) → consumers |
| Hot state | Redis 7 (`StackExchange.Redis`): shared output cache, stock admission gate, idempotency fast path |
| Tests | xUnit + Testcontainers (MsSql, RabbitMq, Redis) + WebApplicationFactory |
| Load tests | k6 scripts under `tools/load/`, run through Docker; results in `docs/capacity.md` |
| Frontend | Angular 18+, standalone components, signals, reactive forms, `EventSource` |

Stack decisions taken during Task 1 (owner instruction): Minimal APIs instead of controllers,
SQL Server 2022 instead of PostgreSQL, and 64-bit Snowflake ids instead of GUIDs. Decisions taken
after Task 4 (owner instruction): **RabbitMQ moves messages, Redis holds hot state, SQL Server
is the truth.** No Kafka, no sharding. All docs and specs are written for that stack.

## Solution layout

```
src/
  Ordering.Domain/          entities, value objects, domain errors. No dependencies.
  Ordering.Application/     CQRS commands/queries + abstractions. Depends on Domain only.
  Ordering.Infrastructure/  EF Core, Dapper, outbox relay, RabbitMQ, Redis, consumers, fake delivery.
  Ordering.Api/             minimal-API endpoints, DI composition, middleware, SSE, rate limits.
tests/
  Ordering.IntegrationTests/  real SQL Server + RabbitMQ + Redis, concurrency + transaction tests
  Ordering.UnitTests/         pure domain/pricing rules
tools/
  load/                     k6 scenarios + the wait-stats script; results go to docs/capacity.md
web/
  order-console/            Angular app
```

Dependency rule: `Api -> Infrastructure -> Application -> Domain`. Never the reverse.
`Application` defines interfaces, `Infrastructure` implements them. No RabbitMQ or Redis type
leaves `Infrastructure/Messaging` and `Infrastructure/Redis`.

## CQRS rules (non-negotiable)

- **Writes go through EF Core** inside one explicit transaction. Commands return `Result<T>`.
- **Reads go through Dapper** with hand-written SQL in `Infrastructure/Read/*QueryRepository.cs`.
  Queries never load EF entities and never open a transaction.
- One handler per file under `Application/Features/<Feature>/<Name>Command|Query.cs`.
  Request, validator and handler live in that one file (vertical slice, KISS).
- Endpoints are thin (Minimal API, one `Api/Endpoints/<Feature>Endpoints.cs` per feature):
  bind → dispatch → `ToHttpResult()`. No `if` on business rules.

## Shared abstractions (build once in Task 1, reuse everywhere)

`Ordering.Application/Abstractions/`

- `Result` / `Result<T>` with `Error(code, message, type)` — no exceptions for expected failures.
- `ICommand<T>`, `IQuery<T>`, `ICommandHandler<,>`, `IQueryHandler<,>`, `IDispatcher`.
- Pipeline behaviours: `ValidationBehavior`, `LoggingBehavior`, `TransactionBehavior`,
  and from Task 14 `AdmissionBehavior` (runs **before** `TransactionBehavior`).
- `IUnitOfWork`, `IClock`, `IIdGenerator`.
- `BaseCommandHandler<TCommand,TResult>` — hosts the transaction/`IUnitOfWork` plumbing so
  concrete handlers only express business intent.
- `BaseDapperRepository` — owns connection creation, `QuerySingleOrDefaultAsync` helpers.
- `ResultExtensions.ToHttpResult(Result)` (`Api/Endpoints`) — the single Result→HTTP mapping in the codebase.
- `IUnitOfWork.OnCommitted(Func<CancellationToken, Task>)` (Task 13) — the only way to run I/O
  after a commit.
- `IEventPublisher` (Task 7), `IChangeHintPublisher` (Task 13), `IStockGate` and
  `IIdempotencyCache` (Task 14) — the only broker/Redis seams `Application` sees.
- `IFailurePoint` (Task 8) — the only test seam in production code: `TransactionBehavior` and
  the consumer report named points; production registers `NoFailurePoint`, tests inject crashes.

If you are about to write the same plumbing a second time, move it into a base class first.

## Correctness rules

1. **Money is `decimal`** everywhere, `decimal(18,2)` in SQL Server. Never `float`/`double`.
2. **Never use a process-local lock** (`lock`, `SemaphoreSlim`, static dictionaries) to protect
   stock, idempotency or cancellation. The database must be correct across N instances.
3. Stock is deducted by a **conditional UPDATE** guarded by a `CHECK (available_quantity >= 0)`
   constraint. Zero rows affected = 409 conflict, not an exception path.
4. Order creation deducts stock, inserts the order, the idempotency row and the outbox message in
   **one transaction**. Partial state is a bug. The stock update is the **last** statement before
   commit, so a hot product row is locked for one round trip, not the whole handler.
5. Idempotency is enforced by a **unique index**, not by a read-then-write check.
6. State transitions use conditional updates (`WHERE status = 'Confirmed'`) or an EF concurrency
   token. Never read → decide → write without a guard.
7. Delivery is **at-least-once** end to end (outbox → RabbitMQ → consumer) with a stable event
   id. Every consumer is idempotent on that id. Document duplicate risk, do not pretend it is
   exactly-once.
8. **Every id comes from `IIdGenerator.NewId()`** (Snowflake, `bigint`, assigned before insert,
   `ValueGeneratedNever()` in EF). No `NEWID()`, no identity columns. `long` is a JSON string on
   the wire and a `string` in TypeScript — 2^53 is smaller than a Snowflake.
9. **Redis may only fast-fail or serve a read.** The stock gate and the idempotency cache may
   produce a 409 or a 200 replay; they may never produce a 201 on their own. Every success path
   still runs the conditional UPDATE and the unique-index insert. Redis down = gate open,
   cache miss, everything goes to the database (fail-open, logged).
10. **No I/O to RabbitMQ or Redis inside a transaction.** The outbox row is the truth for
    events; `IUnitOfWork.OnCommitted` is the only post-commit hook. Change hints on the fan-out
    exchange are best-effort; the truth is always `GET /api/orders/{id}`.
11. **No 500 under load.** When full, the API answers `503` with `Retry-After` — from the rate
    limiter, the stock gate or a lock timeout — never a pool timeout, never an unhandled
    `SqlException`.
12. **No endpoint returns an unbounded list.** Lists are keyset-paged on the server
    (`Page<T>`, `pageSize` ≤ 200, opaque cursor), filtered and sorted in SQL from a whitelist —
    never from the request string — and the browser never holds more than one page. Client-side
    sorting or filtering of server data is a bug.

## Style

- KISS: the simplest thing that satisfies the requirement and its test. No mediator library,
  no repository-per-entity, no generic "framework" nobody asked for.
- DRY: shared behaviour lives in the base classes above or in `lib/` on the frontend.
- SOLID: handlers depend on interfaces from `Application`, not on EF/Dapper/RabbitMQ/Redis types.
- `nullable` enabled, warnings as errors, file-scoped namespaces, no regions.
- Public behaviour is named in domain language: `ConfirmOrder`, `RestoreStock`, not `UpdateData`.
- Measure before you tune: a performance change without a before/after row in
  `docs/capacity.md` is not done.

## Commands

```bash
docker compose up -d                 # SQL Server 2022 :1433 (db-init creates Ordering); RabbitMQ :5672 (UI :15672); Redis :6379
docker compose --profile app up -d --build   # + API image on :5000 and console image (nginx) on :8080
dotnet run --project src/Ordering.Api # API on https://localhost:5001
dotnet test                          # all tests (Testcontainers pulls mssql/server:2022-latest, rabbitmq:4-management, redis:7-alpine)
dotnet ef migrations add <Name> -p src/Ordering.Infrastructure -s src/Ordering.Api
dotnet run --project src/Ordering.Api -- seed --products 200   # load-test catalogue LOAD-0001..; plain `seed` = the two products
docker run --rm -i -e API_BASE=http://host.docker.internal:5000 grafana/k6 run - < tools/load/create-orders.js
cd web/order-console && npm start    # Angular on 4200
```

## Task workflow

Tasks are `/task1` … `/task15`; `docs/tasks/00-overview.md` gives the **run order** (12–15 run
after 8, before 10). Each command file tells you which spec in `docs/tasks/` to read.

Rules for every task:
1. Read `CLAUDE.md`, `docs/architecture.md` and the task spec before writing code.
2. Do not start a task while an earlier one's "Definition of done" is unmet.
3. Finish with `dotnet build` + `dotnet test` green, then tick the boxes in the task file.
4. Never weaken a test to make it pass. If a test is wrong, say so and explain why.
5. Anything you had to assume goes into `docs/assumptions.md` under its task (the root
   `README.md` is the short reviewer-facing version; keep it short).
6. Any throughput claim goes into `docs/capacity.md` with the command that produced it.

## Out of scope

No auth, no deployment, no real payment provider, no sharding, no asynchronous (202) order
intake, no Redis-as-source-of-truth. Payment, multi-region, and moving the stock counter itself
into Redis or into stock buckets are **design-only** sections in the design note (Task 11).
