# Build log — decisions and assumptions per task


The full record of what each task decided and assumed while it was built, kept verbatim. The
short version a reviewer needs is in the root `README.md`; the design decisions are in
`architecture.md`; the measured numbers are in `capacity.md`. Where a task file says
"README, Task N assumptions", it means the section below (this file was the README until the
hand-over).

## Getting started

```bash
docker compose up -d                  # SQL Server 2022 on :1433 (db-init creates Ordering); RabbitMQ 4 on :5672, management UI on :15672 (guest/guest, development only); Redis 7 on :6379
docker compose --profile app up -d --build   # also builds + runs the API (src/Ordering.Api/Dockerfile, :5000) and the console (web/order-console/Dockerfile, nginx :8080 proxying /api)
dotnet tool restore                   # pins dotnet-ef 9.0.x (.config/dotnet-tools.json)
dotnet ef database update -p src/Ordering.Infrastructure -s src/Ordering.Api   # apply migrations
dotnet run --project src/Ordering.Api -- seed   # migrate + seed the two products, then exit
dotnet run --project src/Ordering.Api -- seed --products 100000   # + the load catalogue LOAD-000001…; SqlBulkCopy, seconds, re-run adds nothing
dotnet build                          # net9.0, warnings as errors
dotnet test                           # unit tests + integration tests on a Testcontainers SQL Server (needs Docker)
dotnet run --project src/Ordering.Api # https://localhost:5001 — /health (database + RabbitMQ), /swagger; Development also migrates + seeds on startup
cd web/order-console && npm ci && npm start   # Angular console on http://localhost:4200, /api proxied to the API on :5000; /design is the design reference
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

### Task 8 — required tests on real SQL Server + RabbitMQ

- **Wall time.** `dotnet test` from a rebuild: **56 s** on the development laptop (48 unit tests
  in <1 s; 57 integration tests in 48 s, of which ~25 s is starting the two containers) with the
  `mssql/server:2022-latest` and `rabbitmq:4-management` images already pulled. A truly clean
  clone adds the image pulls (~1.5 GB) on top. Two further runs of the integration project
  passed at the same duration; nothing is skipped and nothing uses `UseInMemoryDatabase`.
- **One collection, one container of each.** `BackendFixture` owns `SqlServerFixture` and the new
  `RabbitMqFixture` (Testcontainers.RabbitMq, `rabbitmq:4-management`, user `ordering` — not
  `guest`, which RabbitMQ only admits from loopback, and Docker's port proxy is not loopback),
  started together, once for the `Backend` collection. Every integration test class joined that
  collection; the old `SqlServer` collection is gone so the run never starts a second SQL
  Server. `ResetAsync` empties the four tables in FK order, re-seeds, then declares the topology
  and purges every queue `RabbitMqTopology` names (main, retry tiers, dead).
- **Tests 2 and 3 already existed** (`IdempotencyTests`, Task 5) and were kept where they are;
  their bodies now use the shared helpers. Tests 1, 5 and the deferred Task 4 multi-line case are
  `CreateOrderTests`; test 4 and the Task 6 boxes are `CancelOrderTests`; the deferred Task 3 pair
  is `ReadSideTests`; test 6 and the at-least-once cases are `NotificationDeliveryTests`
  (suffix `_ThroughBroker`, the slow ones).
- **Shared harness pieces.** `Concurrency.BurstAsync` (one `TaskCompletionSource` barrier, N
  real `HttpClient` calls released together — the inline gate from Task 5 moved here),
  `Concurrency.WaitForAsync` (100 ms poll, 10 s default, throws with the last value),
  `OrdersApi` (`PostOrderAsync` / `CreateOrderAsync` / `CancelOrderAsync` / `GetOrderAsync` on a
  plain `HttpClient`), and Dapper helpers on `SqlServerFixture` (`StockAsync`, `CountAsync`,
  `SnapshotAsync`, `OutboxRowAsync`, `AddProductAsync`). Invariants are asserted from the
  database; ids are parsed with `long.Parse` from the JSON strings.
- **`IFailurePoint` is a production seam with a no-op production implementation.**
  `Application/Abstractions/IFailurePoint.cs` names three points: `transaction.before-commit`
  (consulted by `TransactionBehavior` between `next()` and `CommitAsync`),
  `notification.delivered.before-verdict` and `notification.sent.before-ack` (consulted by
  `NotificationConsumer`). `AddApplication` registers `NoFailurePoint` with `TryAdd`; the test
  host replaces it with `InjectedFailures`, which arms a point one-shot to either **throw**
  (test 5: the request ends in a 500 and SQL Server rolls the transaction back — the SQL log
  shows `UPDATE products` immediately before `ROLLBACK` and no `COMMIT`) or **hold** the caller
  until the host's stopping token fires (the consumer tests: the test then disposes the host,
  which is a process dying with an unacknowledged delivery in hand). Nothing is mocked in either.
- **Per-host test doubles.** `OrderingApiFactory` is one process: its own `Snowflake:WorkerId`,
  `InjectedFailures`, `DeliveryLog` (a `RecordingDeliveryService` wraps the real
  `FakeDeliveryService`, whose failure modes still decide the verdict, and records every call),
  `CapturedLogs` (a Serilog `ILogEventSink` the host picks up through `ReadFrom.Services`) and
  the EF `SqlStatementLog`. `Messaging = true` turns the relay and consumer on; extra
  `Settings` (`Notifications:Delivery:FailureMode`, `Notifications:Consumer:MaxAttempts`) are
  applied per host, so 6a and 6b each run their own host instead of mutating a shared one.
  Two hosts against one backend is how the at-least-once cases are run.
- **At-least-once, now proven rather than described** (rule 7; the owner's question for this
  task). Delivery is outbox → RabbitMQ → consumer with manual acks, so a consumer that dies
  after the send and before the ack is redelivered to. Two tests kill a real host at exactly
  those moments and let the broker redeliver to a second host:
  1. **Died after `Sent` was written, before the ack**
     (`…DiesAfterTheVerdictBeforeTheAck_IsNotSentAgainOnRedelivery`): the second host's
     `attempt_count += 1 … WHERE status = 'Processing'` affects no row, the message is
     acknowledged without a send. Asserted: one send across both hosts, `attempt_count = 1`,
     the "Duplicate delivery" log line on the second host, main queue empty. **This is the
     consumer's idempotency on the event id, and the outbox row is where it lives.**
  2. **Died after the provider accepted the send, before `Sent` was written**
     (`…DiesAfterDeliveryBeforeTheVerdict_IsSentAgainWithTheSameEventId`): nothing in the
     database knows the send happened, so the redelivery is counted and sent again. Asserted:
     two sends, `attempt_count = 2`, both sends carry the **same event id** (`outbox_messages.id`
     = AMQP `MessageId`), one outbox row, and the row ends `Sent`. This window is inherent to
     any at-least-once pipeline whose downstream call and verdict are not one atomic step; the
     design closes it the same way §8 closes payment — the delivery service receives the stable
     event id as its idempotency key and a real provider deduplicates on it. `FakeDeliveryService`
     deliberately keeps no state (rule 2), so the test asserts the *shape* of the duplicate
     rather than hiding it.
  The same two tests are the Task 7 restart-durability proof (a second host picks up where a
  dead one stopped); a third test creates rows while no relay or consumer runs and watches a
  fresh host deliver them.
- **Mutation check, as the Definition of done asks.** Deleting `AND available_quantity >=
  @quantity` from `StockRepository.TryDeductAsync`: test 1 fails, and so do the 50-way race
  (`Expected 49 Actual 0` — the `CHECK` backstop turns the 409s into 500s) and the multi-line
  test. Deleting `AND status = 'Processing'` from `OutboxStore.CountAttemptAsync`: **6a stays
  green** — a clean `FailFirstN` run never produces a duplicate delivery, so 6a cannot observe
  that guard and the task file's expectation there is wrong; the test that goes red is
  redelivery case 1 above, with `Collection: [DeliveryCall { Attempt = 2, Succeeded = True }]`
  — the customer notified twice. Both guards were restored; `git diff` on the two files is empty.
- **Test 5 answers 500.** An exception between the handler and the commit is not an expected
  failure, so it is not a `Result`; `ExceptionHandlingMiddleware` reports it as a 500
  ProblemDetails and the transaction is rolled back. The test also proves the host is healthy
  afterwards: the same key resubmitted immediately gets a 201.
- **The cancel guard is asserted from the SQL log**: two racing cancels produce two
  `UPDATE orders … AND status = @p` statements, exactly one `UPDATE products … + @p` restore and
  two `COMMIT`s (the loser commits an empty transaction and answers 200 from a committed read).

### Task 15 — catalogue paging

- **Built without test code** (owner instruction: "skip tests"), like Tasks 3/4 and 9. The
  Definition-of-done boxes that *are* tests (the 100,000-row fixture, the walk under concurrent
  stock updates, the vary-by-query collision test, the p95 assertion) are left unticked; every
  behaviour behind them was verified by hand against the compose database with 100,002 products
  and the numbers are in `docs/capacity.md` under "Read path". The harness is Task 8's; the
  fixture can call `DbInitializer.SeedLoadCatalogueAsync(context, 100_000, ct)` directly.
- **Keyset, not offset.** `OFFSET n` reads and discards `n` rows on every request — page 1,000
  costs 1,000× page 1 — and shifts under the stock updates that run all day on `products`, so a
  walker sees duplicates or gaps. The cursor is "everything after the last row I saw": one index
  seek per page whatever the depth, stable under inserts and updates. The price is no "jump to
  page 37"; the console gets Previous/Next and a counter, which is what a catalogue needs.
- **Prefix match only.** `search` is `code LIKE 'x%' OR name LIKE 'x%'` (case-insensitive by
  collation), sargable on the clustered key and `ix_products_name`. A contains-match on `name`
  would need full-text search and is out of scope. `%`, `_`, `[` and `\` in the search are
  escaped (`ESCAPE '\'`), so `search=50%` finds `50%_off` and nothing else and `search=[` is a
  bracket, not a character class.
- **The SQL is assembled, but only from fixed fragments.** The sort column comes from a
  three-entry dictionary keyed by the whitelisted field; the direction is `ASC`/`DESC` from a
  bool; the search, stock and seek predicates are appended only when they apply, so each shape
  gets its own sargable plan instead of one plan hedging on `@x IS NULL OR …`. Request values
  travel as parameters only.
- **The price seek parameter is `decimal(18,2)` explicitly** (`Money.Precision`/`Money.Scale`,
  the column's type). A plain `DbType.Decimal` parameter made SQL Server compare the column
  against a wider decimal and walk the index — 41 ms p50 growing with depth instead of 6 ms
  flat. Before/after in `docs/capacity.md`.
- **The cursor is checked, not signed.** `PageCursor` (`Abstractions/Paging`, generic) carries
  `{v,f,d,k,c,h}`; decode fails closed on bad base64url, bad JSON, a missing member or another
  version, and the handler also rejects a cursor whose sort field, direction or filter hash
  (first 8 hex of SHA-256 over `search|inStock`) differ from the request, or whose `k` does not
  parse as the sort column's type. All of these are `400 paging.invalid_cursor` — a plain
  `Error`, not a `ValidationError`, because the fix is "start from page 1", not "change a field".
- **Validation names the query-string parameter** (`errors.pageSize`, `errors.sort`,
  `errors.search`), not the C# property, because that is what the caller has to fix. Out-of-range
  `pageSize` is a 400, never clamped. A `pageSize` that is not an integer at all is rejected by
  Minimal API binding before the pipeline (also 400).
- **`total` is `long?`** and therefore a JSON string on the wire (`LongAsStringJsonConverter`
  applies to `long?` too); the console already types it `number | string | null`. It is counted
  once per new query with `COUNT_BIG` in the same round trip as the first page, and is absent
  (`null`) on every later page. `hasMore` comes from the `pageSize + 1` look-ahead row.
- **Load catalogue.** `seed --products N` bulk-copies `LOAD-000001 … LOAD-{N}` into a temp table
  in batches of 10,000 and inserts only the codes that do not exist yet, so it is guarded per
  code like the two brief products and a re-run adds nothing. Names are `<noun> <colour>` from
  10 × 8 words (100,000 rows → 1,250 per name, plenty of ties and shared prefixes), prices are
  `0.99 + (i × 37 mod 10,000) / 100` (10,000 distinct values, non-monotonic in code order),
  stock is 1,000. 100,000 rows: 4.3 s on the compose database.
- **`available_quantity` is not indexed** and no index carries it: it is written by every order
  and every cancel. `inStock=true` is evaluated on the rows the seek returns (a 50-row key
  lookup per page); with the seed above that costs nothing measurable.
- **Output cache seam.** Task 13 is not built yet; `ProductsEndpoints` carries the `// Task 13:`
  comment with the exact policy line (`SetVaryByQuery("search", "inStock", "sort", "pageSize",
  "cursor")`). The collision test belongs to that task.

### Task 9 — Angular foundation

- **Angular 21, zoneless, signals.** The workspace was generated with the CLI installed on the
  machine (21.x; the spec says 18+). Change detection is zoneless (`ng new --zoneless`), so
  every piece of state is a signal and every component is `OnPush`; there is no `zone.js` in
  the bundle. No tests were written for this task (owner instruction: "skip tests").
- **Same-origin API through the dev-server proxy.** The API has no CORS policy and none is
  added: `ng serve` proxies `/api` and `/health` to `http://localhost:5000`
  (`proxy.conf.json`), and `APP_CONFIG.apiBaseUrl` is `/api`. Same-origin also means the
  browser exposes `Retry-After` to `HttpClient` without an `Access-Control-Expose-Headers`
  header, and `EventSource` needs no `withCredentials` dance. A deployment that serves the
  console from another origin has to add CORS (design-only; deployment is out of scope).
- **`BaseApiService` "unwraps data" is a no-op.** The API returns the DTO as the body, with no
  envelope, so `get<T>`/`post<T>` return the body as `T`. Failures never reach a component as
  `HttpErrorResponse`: `apiInterceptor` maps them once to `ApiError` (ProblemDetails `code`,
  `detail`, every non-standard member as `extensions`, and `Retry-After` in seconds — a
  delta or an HTTP date). A body without `code` becomes `http.<status>`; no response at all is
  `network.unreachable` (status 0); an RxJS `TimeoutError` is `network.timeout`.
- **`isRetryableWithSameKey`** is `status === 0 || status === 503 || code === 'network.timeout'
  || (status === 409 && code === 'idempotency.in_progress')`. Every 503 is included whatever its
  code, because a 503 by rule 11 always means "nothing committed, try again".
- **Only `BaseApiService` may import `HttpClient`** — enforced by ESLint
  (`no-restricted-imports` on `@angular/common/http` / `HttpClient`, with one override for
  `lib/api/base-api.service.ts`), so the rule fails the build rather than a code review.
- **`EventSource` cannot read a status code.** The browser reconnects by itself after a dropped
  connection (`readyState = CONNECTING`) but gives up for good on a non-200 response or wrong
  content type (`readyState = CLOSED`). `BaseEventStream` treats the latter as the API's
  `503 sse.full` and sets `error()` to `ApiError('sse.full', …, 503)`; the owner then falls back
  to `BaseResource.reload()` every `APP_CONFIG.fallbackPollMs` (10 s). A `done` event closes the
  stream and sets `done()`. `BaseEventStream` and `BasePagedResource` must be created in an
  injection context: they stop on the owner's `DestroyRef`.
- **`BaseResource.reload()` cancels the request in flight**, so a slower, older response can
  never overwrite a newer one; `set()` lets an event-stream frame or a command response replace
  the value without a round trip.
- **Status vocabulary.** `lib/ui/status.ts` holds the one status → tone + label map, keyed by
  the API's own strings (`Confirmed`, `Cancelled`, `Pending`, `Sent`, `Failed`, `None`) plus the
  client-side `InsufficientStock`; `ui-badge` and `ui-status-band` read it and nothing else
  decides a colour. Unknown values render verbatim in the neutral tone rather than being hidden.
  The band's colour is the **order** status; the notification outcome is the word beside it
  (`Notification pending · attempt 2`, `Notified after 3 attempts`, `Notification failed after 5
  attempts`).
- **`ui-field` wires the projected control.** Callers write `<input class="control" id="x">`;
  the field finds `#x` after render and sets `aria-invalid` and `aria-describedby` (hint and/or
  error id). The error region is always present with `aria-live="polite"`, so a message that
  appears is announced.
- **`ui-combobox` follows ARIA 1.2**: `role="combobox"` on the input, `aria-expanded`,
  `aria-controls`, `aria-activedescendant`; options are `role="option"` and deliberately not
  tab stops (focus never leaves the input — `mousedown` on an option is prevented). Its id
  input is `inputId`, not `id`, because a plain `id` attribute would land on the host element
  too and the `<label for>` would point at a non-labelable element. The `search` output keeps
  the spec's name and carries a documented exception to `no-output-native`. Typed text that was
  never selected reverts on blur: `selected` is the truth.
- **Paging mirrors Task 15.** `Page<T>.total` is typed `number | string | null` because it is a
  `long` on the wire (`LongAsStringJsonConverter`); `BasePagedResource` normalises it with
  `totalOf()`. `search` is debounced 300 ms inside the resource; every other change requests at
  once; all requests go through one `switchMap`, so the newest always wins. A
  `400 paging.invalid_cursor` on a non-first page restarts at page 1 of the same query with no
  alert. The design reference drives the three paged components from an in-memory
  `BasePagedResource` over 10,000 generated rows with a base64 keyset cursor of the same shape
  as the server's (`{v,f,d,k,c,h}`), so the demo exercises stale-cursor rejection too.
- **Layout classes live in `styles.scss`** (`.console`, `.console__action`, `.console__detail`,
  `.console__stock`, `.control`, `.data`, `.section-title`) so Task 10's feature components drop
  into the shell without re-declaring the grid. The route `''` renders the shell with `// Task
  10:` seams; `/design` is the design reference.
- **Verified by driving the app**, not only by building it: headless Chrome over the DevTools
  protocol at 1440/1024/400/360 px (no console errors, no horizontal overflow at 360 even with a
  15 px classic scrollbar), the combobox keyboard pass (type → 10 options; ↓↓ → option 1 via
  `aria-activedescendant`; Enter selects and closes; ↓ reopens; Escape closes; focus stayed on
  the input throughout), Next/Previous, header sort (`aria-sort` follows), and prefix search.

