# Concurrent Order Processing

An order service where stock can never go negative, submissions are idempotent, cancellation
restores stock exactly once, and order-created notifications are delivered by a background worker
with retries. `CLAUDE.md` is the engineering contract; `docs/architecture.md` holds the design
decisions; `docs/tasks/` is the build plan.

> Task 11 expands this file into the full README and the design note. Until then it records the
> commands that work today and the assumptions each task had to make.

## Getting started

```bash
docker compose up -d                  # SQL Server 2022 on :1433 (db-init creates Ordering); RabbitMQ 4 on :5672, management UI on :15672 (guest/guest, development only)
dotnet tool restore                   # pins dotnet-ef 9.0.x (.config/dotnet-tools.json)
dotnet ef database update -p src/Ordering.Infrastructure -s src/Ordering.Api   # apply migrations
dotnet run --project src/Ordering.Api -- seed   # migrate + seed the two products, then exit
dotnet build                          # net9.0, warnings as errors
dotnet test                           # unit tests + integration tests on a Testcontainers SQL Server (needs Docker)
dotnet run --project src/Ordering.Api # https://localhost:5001 — /health (database + RabbitMQ), /swagger; Development also migrates + seeds on startup
```

Notification delivery is simulated; switch the failure mode without editing files, e.g.
`Notifications__Delivery__FailureMode=FailFirstN dotnet run --project src/Ordering.Api`
(`None | AlwaysFail | FailFirstN | Random`, see `appsettings.json`).

The API connection string lives in `src/Ordering.Api/appsettings.json` and matches the compose
credentials (`sa` / `Ordering!Passw0rd`). Development-only; there is no auth in scope.

## Stack decisions made during Task 1

All three were owner instructions; every spec under `docs/` is written for this stack.

| Original brief | Now | Notes |
|---|---|---|
| ASP.NET Core controllers, `ApiControllerBase.ToActionResult` | Minimal APIs, `Api/Endpoints/ResultExtensions.ToHttpResult` | Still one Result→HTTP mapping; endpoints stay bind → dispatch → map. |
| PostgreSQL 16, Npgsql, Testcontainers.PostgreSql | SQL Server 2022, Microsoft.Data.SqlClient, Testcontainers.MsSql | Row-lock semantics of the conditional `UPDATE` are the same; worker claiming uses `UPDLOCK, READPAST` instead of `SKIP LOCKED`. |
| GUID v7 ids | 64-bit Snowflake ids (`SnowflakeIdGenerator`) | `bigint` keys that append in clustered order; the event id is known before the outbox row is written. |

## Scope decisions made after Task 4

Owner instructions, taken when the build plan was extended for load (`docs/tasks/00-overview.md`,
Tasks 12–14). The brief's hard rules are unchanged; these say where throughput comes from.

| Decision | Instead of | Consequence |
|---|---|---|
| **RabbitMQ** carries outbox events to the notification consumers and best-effort change hints to the API instances (Tasks 7, 13) | Kafka; the in-process polling worker of the original brief | The outbox row stays the truth for every event; the relay never sleeps on a full batch; delivery scales with consumer instances. Broker on `5672`, UI on `15672`, `guest`/`guest` in development. |
| **Redis** holds hot state: the shared catalogue cache (Task 13), the stock admission gate and the idempotency fast path (Task 14) | Nothing — every request hit SQL Server | Redis may answer 409 or a 200 replay on its own; it may **never** answer 201. Every success still runs the conditional `UPDATE` and the unique-index insert. Redis down = gate open, cache miss, not an outage. |
| **One SQL Server, no sharding** | Sharded order storage | The database remains the only source of truth for stock and orders (`CLAUDE.md` rule 3). Hot-product throughput is bounded by the row lock hold time, which Task 12 minimises and measures. Moving the counter itself into Redis or into stock buckets is design-only (design note §5). |
| **Synchronous 201/409 kept** | 202 Accepted + asynchronous intake | The brief and Task 8's tests require the 409 to be the response. Bursts are absorbed by fast rejection (gate, rate limiter) rather than by queueing orders. |

## Assumptions

### Task 1 — foundation

- **SDK.** `global.json` pins 9.0.300 with `rollForward: major`, so a machine with only a newer SDK
  still builds `net9.0`. Package versions are pinned centrally in `Directory.Packages.props`;
  `Microsoft.*` packages stay on 9.x to match the target framework.
- **Database creation.** SQL Server has no `POSTGRES_DB` equivalent, so `docker-compose.yml` runs a
  one-shot `db-init` job that creates the `Ordering` database once `db` is healthy. Migrations (Task
  2) will also create it, so the job is a convenience, not a dependency.
- **Transaction ownership.** `TransactionBehavior` opens/commits/rolls back and applies to commands
  only (enforced by the `IBaseCommand` generic constraint, which the DI container honours).
  `BaseCommandHandler` only calls `SaveChangesAsync` on a success result. `IUnitOfWork.Commit` and
  `Rollback` are specified as no-ops when no transaction is active so a handler may roll back early
  (the idempotency replay path in Task 5) without breaking the behaviour.
- **Failures inside generic pipeline code** are built with `IResultFactory<TSelf>.Failure` (a static
  abstract member) rather than reflection, so `ValidationBehavior` is type-safe for both `Result`
  and `Result<T>`.
- **Validation errors** keep FluentValidation's property names as-is (PascalCase, e.g.
  `Lines[0].Quantity`) in the ProblemDetails `errors` map. Every failure ProblemDetails also carries
  the stable error code under `extensions.code`.
- **Dapper naming.** `DefaultTypeMap.MatchNamesWithUnderscores = true` is set once so snake_case
  columns map onto PascalCase read models.
- **Ids.** `SnowflakeIdGenerator` packs `41-bit ms since 2024-01-01 | 10-bit worker | 12-bit
  sequence`. It is lock-free (a single CAS over a packed timestamp/sequence word), never blocks
  waiting for the clock — a millisecond that runs out of sequence numbers borrows the next one —
  and ignores a clock that steps backwards, so ids are unique and strictly increasing per worker
  (covered by a 32-thread barrier-released race test). **Each running instance must set a distinct
  `Snowflake:WorkerId` (0–1023)**; `appsettings.json` ships with `0` for local development. This
  is the cross-process uniqueness guarantee — there is no database coordination for it.
- **Ids on the wire.** Snowflake values exceed JavaScript's 2^53, so `LongAsStringJsonConverter`
  writes every `long` as a JSON string and accepts it back as string or number; Swagger declares
  `long` as `string`/`int64`. TypeScript models must keep ids as `string`.
- **Swagger** is enabled in every environment: there is no auth and no deployment in scope, and the
  smoke test relies on it.
- **Health.** `/health` runs `SELECT 1` on the configured database, so it is red until
  `docker compose up -d` has finished.
- **Test placement.** Anything that needs the `Api` or `Infrastructure` assembly (Result→HTTP
  mapping, Snowflake generator, host smoke test) lives in `Ordering.IntegrationTests` even when
  it needs no database; `Ordering.UnitTests` references only `Domain` and `Application`.

### Task 2 — domain and write model

- **`Error` lives in `Ordering.Domain.Common`, not `Application.Abstractions`.** The Task 2 spec
  puts `OrderErrors` (static `Error` factories) in Domain, and Domain may reference nothing, so the
  `Error`/`ErrorType` types moved down one layer (`CLAUDE.md` already lists "domain errors" under
  Domain). `Result`/`Result<T>` stay in `Application.Abstractions` and carry a `Domain.Error`.
  Consumers only gained a `using Ordering.Domain.Common;`.
- **Domain invariants throw; expected failures are `Result`s.** `Order.Create`/`OrderLine.Create`
  reject bad input (`quantity <= 0`, blank or >64-char customer reference, no lines, a line of
  another order, a product listed twice) with `ArgumentException`s, and `Order.Cancel` from a
  non-`Confirmed` state throws `InvalidOperationException`. These are programming errors — the
  request validator (Task 4) and the guarded `UPDATE ... WHERE status = 'Confirmed'` (Task 6) stop
  callers from reaching them — whereas `OrderErrors.*` are the expected failures handlers return.
- **Rounding.** `LineTotal = round(quantity × unitPrice, 2)` with `MidpointRounding.AwayFromZero`
  (commercial rounding: 0.125 → 0.13) and `Total = Σ LineTotal`, so the stored `total` always
  equals the sum of the stored `line_total`s. With catalogue prices at 2 dp the two readings of
  "Σ(quantity × unitPrice) rounded to 2 dp" give the same number; the choice only matters for
  synthetic 3-dp prices in unit tests.
- **`row_version` is an EF shadow property.** The domain `Order` has no persistence field; the
  concurrency token is configured in `OrderConfiguration` (`Property<byte[]>("RowVersion")
  .IsRowVersion().IsRequired()`) and read via `context.Entry(order).Property("RowVersion")`.
- **Extra index.** EF creates an index for every foreign key, so the migration also has
  `ix_order_lines_product_code` (named to match the spec's `ix_` convention). It is harmless and
  not part of any correctness argument.
- **Explicit snake_case names.** Every table, column and constraint name is set in the
  `IEntityTypeConfiguration`s rather than through a naming-convention package, so the migration
  matches the DDL in the spec 1:1 and no extra dependency is needed.
- **Seed and migrate on startup are one switch.** `Database:InitializeOnStartup` (true only in
  `appsettings.Development.json`) makes the host run `DbInitializer.InitializeAsync` — migrate then
  seed — before listening; `dotnet run -- seed` does the same and exits. Seeding is one guarded
  `INSERT ... WHERE NOT EXISTS` per product, so it never resets stock of an existing product.
  `WebApplicationFactory` hosts run as Development, so the DB-free `ApiSmokeTests` set the switch
  to `false`; the Task 8 `OrderingApiFactory` can leave it on against its container.
- **Real database in tests from now on.** `SqlServerFixture` (Testcontainers.MsSql,
  `mcr.microsoft.com/mssql/server:2022-latest`, one container per collection, migrated once) backs
  the schema, seed, EF-mapping and unit-of-work tests. `dotnet test` therefore needs Docker; the
  compose database is not touched by tests. Task 8 adds `ResetAsync` and `OrderingApiFactory` on
  top of this fixture.
- **`dotnet-ef` is a local tool** (`.config/dotnet-tools.json`, 9.0.20 to match the runtime);
  run `dotnet tool restore` once. `Microsoft.EntityFrameworkCore.Design` is referenced by the API
  project with `PrivateAssets="all"` purely so `dotnet ef ... -s src/Ordering.Api` can build the
  `DbContext` from the real DI graph.
- **`Product` has no stock methods.** Deduct/restore are conditional `UPDATE`s in the database
  (Tasks 4 and 6), never in-memory mutations, so the entity deliberately exposes none.

### Task 3 — read side

- **Built together with Task 4, without test code** (owner instruction: "skip write test code").
  The `Integration test:` boxes in both task files are therefore left unticked and belong to Task
  8's harness; every other box was verified by hand against the compose database.
- **Repository interfaces live next to their feature**, not in `Abstractions/`:
  `Features/Products/{IProductQueryRepository,IProductRepository,IStockRepository}.cs` and
  `Features/Orders/{IOrderQueryRepository,IOrderRepository}.cs`. `Abstractions/` stays for
  cross-cutting plumbing; Infrastructure implements the feature interfaces (`Read/` for Dapper,
  `Persistence/` for EF).
- **Dapper materialises the positional DTO records through their constructor**, so every read
  query aliases its columns to the record's parameter names *in parameter order*
  (`available_quantity AS AvailableQuantity`). `MatchNamesWithUnderscores` from Task 1 stays on
  but is not relied upon.
- **`notificationStatus` vocabulary** is `Pending` (outbox `Pending` or `Processing`), `Sent`,
  `Failed`, mapped once in `NotificationStatus.FromOutbox` (Application). `None` is only
  reachable for an order without an `order.created` row, which the create path never produces.
- **`DispatcherTests` no longer sets `ValidateOnBuild`.** Its container is `AddApplication()`
  plus fakes for the pipeline; from Task 3 on `AddApplication()` registers real handlers whose
  repositories are Infrastructure types, so eager validation of that partial graph can never
  pass. The assertions are unchanged, handlers still resolve per `Send`, and the full graph is
  validated by the host in `ApiSmokeTests`.

### Task 4 — create order

- **`Idempotency-Key` is validated here, stored in Task 5.** The header is bound in the
  endpoint, carried on `CreateOrderCommand.IdempotencyKey`, and rejected with 400 when missing or
  not GUID-shaped (`Guid.TryParse`) / ULID-shaped (26 Crockford base32 characters). The validation
  error is keyed `Idempotency-Key`, not the property name.
- **Duplicate product codes are rejected, not merged** (400, `Lines`). Two codes count as the
  same when they differ only by letter case or trailing whitespace — that is how SQL Server's
  default collation matches `products.code`, so being stricter in C# would let a request reach the
  database as one product with two lines. The handler also uses the catalogue's spelling of the
  code on the stored line (`sku-001` is stored as `SKU-001`) and trims the customer reference.
- **The 201 body is built in memory from the aggregate**, not re-read through the Task 3 query:
  a Dapper read on a second connection would block on the transaction's own uncommitted rows.
  It carries `notificationStatus: "Pending"` / `notificationAttempts: 0`, which is exactly what
  `GET /api/orders/{id}` reports until the worker runs. Task 5's replay path is the one that loads
  through the read query.
- **Lock order is the database's, not C#'s.** `IProductRepository.GetByCodesAsync` returns rows
  `ORDER BY code` and the handler deducts in that order, so create/create and (Task 6)
  create/cancel take product row locks in the same collation order and cannot deadlock. Sorting in
  C# with an ordinal comparer could disagree with the collation for non-ASCII codes.
- **`available` in the 409 body is a second read** after the conditional `UPDATE` affected zero
  rows, so it is the committed value at that moment, not a pre-update snapshot. Structured error
  data travels on `Error.Details` (Domain) and `ResultExtensions` copies it into ProblemDetails
  `extensions` generically, so the HTTP mapping stays the single place it was.
- **`OutboxMessage` is an Application type** (`Abstractions/Outbox/`) mapped by EF in
  Infrastructure; the handler stages it through `IOutbox.Enqueue` and it is written by the same
  `SaveChanges` as the order. Payloads are camelCase JSON with `long`s as strings —
  `LongAsStringJsonConverter` moved from `Api` to `Application/Abstractions/Serialization` so the
  API and the outbox share it. `OrderCreated` (Application) is the payload record Task 7 will
  deserialize.
- **`outbox_messages.attempt_count` has an unnamed default constraint.** EF Core 9 cannot name
  default constraints, so the spec's `df_outbox_attempts` is the only DDL name not reproduced;
  the application always writes `0` explicitly anyway.
- **Unreadable bodies are 400 ProblemDetails, not 500.** Minimal APIs throw
  `BadHttpRequestException` for malformed JSON or a missing body when `ThrowOnBadRequest` is on
  (the Development default) and write an empty 4xx otherwise; `ExceptionHandlingMiddleware` now
  maps that exception to a ProblemDetails with the framework's status code so both environments
  answer the same way. Found during the manual review of this task, since it is the first
  endpoint that binds a body.
- **Filtered indexes need `QUOTED_IDENTIFIER ON` for DML.** SqlClient (EF, Dapper) sessions
  have it on by default, so the app and tests are unaffected — but a raw `sqlcmd` session must be
  started with `-I` or any `DELETE`/`UPDATE` on `outbox_messages` fails with error 1934.

### Task 5 — idempotency

- **Payload equivalence.** `request_hash` is the SHA-256 (lower-case hex, 64 characters) of the
  canonical form of the submission, computed by `Application/Idempotency/RequestFingerprint`:

  ```
  customerReference.Trim() + "|" + join(",", lines ordered by CODE, each "CODE:QTY")
  where CODE = productCode.Trim().ToUpperInvariant() and QTY is the integer quantity
  ```

  Same goods, same customer, any JSON key order, whitespace or product-code casing → same hash →
  `200 OK` replay. A changed quantity, product or customer reference → different hash →
  `409 idempotency.key_reuse`. The customer reference is compared **case-sensitively** (`CUST-1`
  and `cust-1` are different customers); product codes are upper-cased because the catalogue's
  spelling is not known at hash time. The key itself, other headers and the URL play no part.
- **The key column is case-insensitive.** `idempotency_key` is `nvarchar(128)` under the
  database's default collation, so `abc…` and `ABC…` are the same key. GUIDs and ULIDs are not
  case-sensitive identifiers anyway; the value is stored as received.
- **`SET LOCK_TIMEOUT 3000` is issued by `TransactionBehavior`, not by the handler.** The spec
  puts it as step 1 of `CreateOrder`; it is still the first statement inside the transaction
  (`IUnitOfWork.SetLockTimeoutAsync`, called right after `BeginTransaction`), but every command
  gets it — Task 6's cancel included — instead of each handler remembering to. Queries never see
  it. The value is `TransactionBehavior.LockTimeout` (3 s).
- **The handler calls `SaveChangesAsync` itself** so the batch runs *before* the stock update;
  `BaseCommandHandler`'s final save on success then has nothing left to flush (no round trip).
  `IUnitOfWork.RollbackAsync` also clears the EF change tracker, so after the replay path rolls
  back, the rows staged for the duplicate cannot be re-inserted by that final save.
- **SQL errors are translated in exactly one place** (`Infrastructure/Persistence/SqlErrors`):
  2627/2601 → `UniqueViolationException(ConstraintName)` (name parsed from the message after the
  number check), 1222 → `LockTimeoutException(Resource)`. The resource is named by the call site:
  `UnitOfWork.SaveChangesAsync` reports `idempotency_keys` (the only key in the create batch that
  can already exist — every other id is a fresh Snowflake) and `StockRepository.TryDeductAsync`
  reports `products`. Anything else is still a 500 via `ExceptionHandlingMiddleware`.
- **Retryable failures.** `ErrorType.Unavailable` maps to `503`. `RetryableError` carries
  `retryAfterSeconds` on `Error.Details`, which `ResultExtensions` turns into a `Retry-After`
  header on any status (it also stays in the ProblemDetails extensions). Used by
  `503 stock.busy` (product row locked past the timeout, nothing committed) and
  `409 idempotency.in_progress` (a duplicate is still inside its transaction). Both are safe to
  retry with the same key.
- **A replay returns the order's current state, not a snapshot of the original response.**
  After a cancel, the replay answers `200` with `status: "Cancelled"` and never re-creates or
  re-confirms. The replay loads through the Task 3 Dapper query on its own connection because
  the handler asks after it has rolled back.
- **Interceptors come from DI.** `AddDbContext` now adds every `IInterceptor` registered in the
  host (`options.AddInterceptors(provider.GetServices<IInterceptor>())`); production registers
  none. The test host registers `SqlStatementLog` (commands + commit/rollback, in order), which
  is how the "stock update is the last statement before `COMMIT`" box is asserted.
- **Until Task 12 (RCSI), an exclusive lock on a product row also blocks the catalogue read.**
  Under plain `READ COMMITTED` the handler's `SELECT … FROM products` needs a shared lock, so a
  writer holding the row for more than 3 s would surface as a lock timeout on that read — which
  is not translated and would be a 500 today. The `503 stock.busy` test therefore holds an
  **update lock** (`WITH (UPDLOCK, ROWLOCK)` in an open transaction): compatible with the
  catalogue's shared lock, incompatible with the conditional `UPDATE`, so the request reaches the
  stock statement, waits ~3 s and gets `503` + `Retry-After: 1` with no rows committed. Task 12
  turns on `READ_COMMITTED_SNAPSHOT`, after which an exclusive lock behaves the same way.
- **Test harness grew the pieces Task 5 needed.** `OrderingApiFactory` (real host on the
  fixture's container, `Snowflake:WorkerId = 7`, Development settings so startup migrates and
  seeds) and `SqlServerFixture.ResetAsync()` (delete `order_lines`, `idempotency_keys`,
  `outbox_messages`, `orders`, `products`, then re-seed) exist now; Task 8 adds the RabbitMQ side
  to both. `DbInitializerTests` start from `ResetAsync()` because the idempotency tests leave
  orders behind on purpose (each works on a product of its own and asserts per-key/per-product).
- **Cancel-then-replay is tested against a hand-applied cancel** (`UPDATE orders SET status =
  'Cancelled' … WHERE status = 'Confirmed'`) until Task 6 adds the endpoint; the test carries a
  `// Task 6:` marker.
- **`FindAsync` returning nothing after a key collision** (the row we collided with vanished
  before we could read it) is answered as `409 idempotency.in_progress`. It cannot happen without
  a manual delete, but the handler must return *something* retryable rather than throw.

### Task 6 — cancel

- **Built without test code** (owner instruction: "skip tests"). The two test boxes in the task
  file are left for Task 8's harness; the rest was verified by hand against the compose
  database (see the note under the task's Definition of done). `IdempotencyTests`'s
  cancel-then-replay case still applies the guarded `UPDATE` by hand and keeps its `// Task 6:`
  marker for Task 8 to switch to the endpoint.
- **The status column is the lock; the handler never decides in C#.** `IOrderRepository.
  TryCancelAsync` runs `UPDATE orders SET status = 'Cancelled', cancelled_at = @now WHERE id = @id
  AND status = 'Confirmed'` as raw parameterized SQL on the transaction's connection and returns
  rows affected. `1` → this request restores stock; `0` → one committed read through the Task 3
  query tells `200` (already `Cancelled`, idempotent, nothing written) from `404`. Of N racing
  cancels exactly one sees `1`, so stock is restored exactly once. `OrderErrors.AlreadyCancelled`
  from Task 2 is therefore unused by the endpoint and kept only for its catalogue test.
- **The 200 body on the cancelling branch is read back inside the transaction.** The Task 4 rule
  (a Dapper read on a second connection would block on our own uncommitted order row — or, once
  RCSI is on, would still see `Confirmed`) applies here too, but a cancel has no aggregate in
  memory to build the DTO from. So `IOrderRepository.ReadBackAsync` runs the Task 3 projection
  (`OrderQueryRepository.GetByIdAsync`, made reusable on a caller-supplied connection) through
  Dapper on the EF connection and transaction. One round trip, the exact `GET /api/orders/{id}`
  shape including the live `notificationStatus`, and the lines arrive `ORDER BY product_code`,
  which is the restore order. The read runs *between* the status flip and the restores; the
  restores are still the last statements before `COMMIT`. Being Dapper on the EF connection, this
  read does not pass through EF interceptors (the test `SqlStatementLog` will not list it).
- **Restore is one `UPDATE products SET available_quantity += @qty WHERE code = @code` per line**,
  in product-code order — the same collation order create deducts in — so a cancel racing a
  create on overlapping products cannot deadlock (50-round race above: zero 1205). The
  repository returns rows affected like `TryDeductAsync`; the handler throws
  `InvalidOperationException` on anything but `1`, because `fk_order_lines_products` makes that
  a broken catalogue, not a business outcome (500 via the middleware, transaction rolled back).
- **Two lock timeouts, two 503s.** A product row held past `LOCK_TIMEOUT` during a restore is
  `503 stock.busy` + `Retry-After: 1`, exactly as in create, and the status flip rolls back with
  it. The order row itself can also be held past the timeout — the winner of a cancel race holds
  it while its restores wait on product rows — so `TryCancelAsync` translates 1222 against
  `orders` and the handler answers `503 order.busy` + `Retry-After: 1` (`OrderErrors.OrderBusy`,
  new) rather than a 500. Both are safe to retry; nothing was committed.
- **No validator on `CancelOrderCommand`.** The route constraint `{id:long}` already types the
  id; an id that cannot exist (0, negative) is a `404`, the same answer `GET` gives.
- **Cancelling does not touch the notification (known, accepted).** The `order.created` outbox
  row is neither removed nor changed. If it is still `Pending` when the order is cancelled, the
  relay (Task 7) still publishes it and the consumer still delivers it — the created event really
  did happen. The cancel response and `GET` simply keep reporting whatever that row's status is.

### Task 7 — outbox relay, RabbitMQ, notification consumers

- **Built without test code** (owner instruction: "skip test"). Every Definition-of-done box
  was verified by hand against the compose stack (see the note under the task's boxes); the
  automated versions belong to Task 8's harness, which the spec already assigns the
  `RabbitMqFixture`, the `Notifications:Relay:Enabled` / `Notifications:Consumer:Enabled`
  switches (added now, both `true` by default) and `ResetAsync`. The existing test hosts
  (`OrderingApiFactory`, the smoke host) set both switches to `false`: they have no broker,
  and the Task 1–6 tests assert on outbox rows the relay would otherwise claim.
- **`RabbitMQ.Client` 7.2.2**, the async `IChannel` API; `Testcontainers.RabbitMq` 4.15.0 is
  pinned next to `Testcontainers.MsSql` for Task 8. `Microsoft.Extensions.Hosting.Abstractions`
  gives Infrastructure `BackgroundService`.
- **Only `Infrastructure/Messaging` sees RabbitMQ.** `RabbitMqConnection` (one `IConnection`
  per process, opened lazily so the host starts without the broker, automatic + topology
  recovery on, `ClientProvidedName = ordering-api/{Snowflake:WorkerId}`), `RabbitMqTopology`
  (names and the idempotent declares), `RabbitMqEventPublisher` (`IEventPublisher`, the relay's
  channel) and `RabbitMqNotificationQueue` (the consumer's channel: prefetch, manual acks, the
  retry re-publish). The consumer in `Infrastructure/Notifications` works with an
  `InboundMessage` record (`MessageId`, `Type`, `x-aggregate-id`, body bytes) and answers
  `Ack | Dead`; it never touches a channel or a queue name, which is what the `CLAUDE.md`
  boundary asks for. The architecture test now also forbids `RabbitMQ.Client` and
  `StackExchange.Redis` in Application.
- **Seams `Application` sees.** `Abstractions/Messaging/IEventPublisher.PublishAsync(
  IntegrationEvent, ct)` — `IntegrationEvent(Id, Type, AggregateId, Payload)` is the claimed
  row's projection, since the EF `OutboxMessage` cannot be materialised by Dapper — and
  `Notifications/INotificationDeliveryService.SendAsync(eventId, attempt, customerReference,
  total, ct)` returning `DeliveryResult(Succeeded, Error)`.
- **Outbox bookkeeping is not a command.** `Infrastructure/Outbox/OutboxStore` runs one
  parameterised, autocommitted T-SQL statement per call on its own connection (it derives from
  `BaseDapperRepository` for the connection plumbing, nothing else): claim, publish mark,
  release, reap, attempt count, `Sent`, retry, `Failed`. No transaction, no EF entity, no
  `IDispatcher`, no pipeline behaviours. Every `@now` comes from `IClock` — including the claim's
  `next_attempt_at <= @now` and `claimed_until = @now + lease`, where the spec shows
  `SYSDATETIMEOFFSET()` — so a test with a controllable clock steers the whole table.
- **The relay publishes a batch concurrently, then marks it in one statement.** Per-row
  publish → confirm → `UPDATE` cost two network round trips per message and measured ~25
  messages/s through Docker Desktop. The relay now awaits all publishes of a claim together on
  its one confirmed channel (confirms pipeline; `IChannel` is safe for concurrent publishing in
  7.x), then runs `UPDATE … SET published_at … WHERE id IN @ids` once and releases the failures
  once — 1,000 `Pending` rows are published in ≈1.9 s from a cold start (all 1,000 `Sent`,
  `attempt_count = 1`; the same with two relays and two consumers on one database and broker).
  The trade-off: a relay that dies between the confirms and the mark leaves up to one batch
  (200 rows) for the reaper to re-publish instead of one row — duplicates the consumer's guard
  absorbs, and at-least-once either way.
- **The publish mark is guarded by `published_at IS NULL`, not by `status = 'Processing'`.**
  With a local broker the consumer regularly delivers and writes `Sent` *before* the relay's
  mark runs; the spec's status guard then skips the row, leaving `Sent` rows with
  `published_at NULL` and the lease columns still set (808 of 1,000 in the first measurement).
  A row the broker confirmed was published whatever its verdict, so the mark now depends only
  on not having been marked yet, and the consumer's `Sent`/`Failed` writes also clear
  `claimed_by`/`claimed_until`. The row-state table in the spec holds as written.
- **Retry tiers and the reaper.** `next_attempt_at` is written by the consumer as
  `IClock.UtcNow + min(BaseBackoffMs × 2^(attempt−1), MaxBackoffMs)` — 200, 400, 800, 1600 ms
  with the defaults — and the same value is each tier queue's `x-message-ttl`. There is no
  jitter: tiers are fixed queues, one per attempt, because RabbitMQ expires only from the head
  of a queue. The consumer also refreshes `published_at` when it schedules a retry, so the
  reaper's `PublishedTimeoutSeconds` counts from the latest re-publish rather than the first;
  with the defaults a message spends at most ≈3 s in tiers, far below the 300 s timeout.
  Changing `MaxAttempts`, `BaseBackoffMs` or `MaxBackoffMs` changes the tier queues' arguments,
  and RabbitMQ refuses a re-declare with different arguments (406 `PRECONDITION_FAILED`): delete
  the `notifications.retry.a*` queues in the management UI first. The relay reaps once at
  startup, then every `ReaperIntervalSeconds`.
- **Broker down.** The relay releases the claimed batch to `Pending` and waits 5 s before the
  next claim; the consumer reconnects every 5 s; `/health` reports `Unhealthy`. Nothing is lost:
  rows stay in the table and go out in `occurred_at` order on the next successful claim. A
  handler exception inside the consumer (typically the database being unreachable *after* the
  message arrived) is logged, waited out for 2 s and NACKed with requeue — the attempt statement
  either ran or did not, so the redelivery is safe.
- **At-least-once, written down.** The delivery call always receives `outbox_messages.id` — a
  Snowflake assigned before the row was inserted, carried as the AMQP `MessageId` and identical
  across retries, tiers and re-publishes — so a real `INotificationDeliveryService` can
  deduplicate on it. Cases where the same id is delivered twice: (1) the consumer dies after
  `SendAsync` succeeded and before `Sent` is written → the broker redelivers → the attempt is
  counted again and the notification is sent again; (2) the consumer dies between the retry
  re-publish and the ACK of the original → both copies arrive → one extra attempt, still
  bounded by `MaxAttempts`; (3) the relay dies between the broker's confirm and the publish
  mark, or a verdict never arrives within `PublishedTimeoutSeconds` → the reaper re-publishes →
  a second copy, acknowledged without sending if the verdict did arrive meanwhile
  (`attempt_count += 1 … WHERE status = 'Processing'` returns no row). `FailFirstN` is
  deterministic across all of these because the attempt number comes from the row, not from
  the fake.
- **Serilog drops any log template with an `{EventId}` placeholder** — its MEL provider adds
  its own `EventId` property to every event, the duplicate makes the write throw, and the
  provider swallows it. Found because the consumer worked but logged nothing; every outbox log
  line now uses `{OutboxEventId}`.
- **Cancelled orders are still notified** (Task 6 decision, unchanged): the `order.created`
  row is published and delivered regardless of the order's later status.
