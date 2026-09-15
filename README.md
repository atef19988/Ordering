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
dotnet build                          # net9.0, warnings as errors
dotnet test                           # unit + integration tests (Task 1 needs no database)
dotnet run --project src/Ordering.Api # https://localhost:5001 — /health, /swagger
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
