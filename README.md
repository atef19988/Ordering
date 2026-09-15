# Concurrent Order Processing

An order service where stock can never go negative, submissions are idempotent, cancellation
restores stock exactly once, and order-created notifications are delivered by a background worker
with retries. `CLAUDE.md` is the engineering contract; `docs/architecture.md` holds the design
decisions; `docs/tasks/` is the build plan.

> Task 11 expands this file into the full README and the design note. Until then it records the
> commands that work today and the assumptions each task had to make.

## Getting started

```bash
docker compose up -d                  # SQL Server 2022 on localhost:1433; db-init creates the Ordering database
dotnet tool restore                   # pins dotnet-ef 9.0.x (.config/dotnet-tools.json)
dotnet ef database update -p src/Ordering.Infrastructure -s src/Ordering.Api   # apply migrations
dotnet run --project src/Ordering.Api -- seed   # migrate + seed the two products, then exit
dotnet build                          # net9.0, warnings as errors
dotnet test                           # unit tests + integration tests on a Testcontainers SQL Server (needs Docker)
dotnet run --project src/Ordering.Api # https://localhost:5001 — /health, /swagger; Development also migrates + seeds on startup
```

The API connection string lives in `src/Ordering.Api/appsettings.json` and matches the compose
credentials (`sa` / `Ordering!Passw0rd`). Development-only; there is no auth in scope.

## Stack decisions made during Task 1

All three were owner instructions; every spec under `docs/` is written for this stack.

| Original brief | Now | Notes |
|---|---|---|
| ASP.NET Core controllers, `ApiControllerBase.ToActionResult` | Minimal APIs, `Api/Endpoints/ResultExtensions.ToHttpResult` | Still one Result→HTTP mapping; endpoints stay bind → dispatch → map. |
| PostgreSQL 16, Npgsql, Testcontainers.PostgreSql | SQL Server 2022, Microsoft.Data.SqlClient, Testcontainers.MsSql | Row-lock semantics of the conditional `UPDATE` are the same; worker claiming uses `UPDLOCK, READPAST` instead of `SKIP LOCKED`. |
| GUID v7 ids | 64-bit Snowflake ids (`SnowflakeIdGenerator`) | `bigint` keys that append in clustered order; the event id is known before the outbox row is written. |

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
