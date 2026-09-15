# CLAUDE.md — Concurrent Order Processing

Read this file before any task. It is the contract. `docs/tasks/*.md` say *what* to build,
this file says *how* to build it everywhere.

## What we are building

An order service where stock can never go negative, submissions are idempotent, cancellation
restores stock exactly once, and order-created notifications are delivered by a background
worker with retries. Plus a small Angular console to drive it.

## Stack

| Layer | Choice |
|---|---|
| API | ASP.NET Core 9, Minimal APIs, ProblemDetails |
| Write model | EF Core 9 + Microsoft.EntityFrameworkCore.SqlServer |
| Read model | Dapper (raw SQL, read-only) |
| Database | SQL Server 2022 (docker compose) |
| Messaging | Transactional outbox table + in-process hosted worker |
| Tests | xUnit + Testcontainers.MsSql + WebApplicationFactory |
| Frontend | Angular 18+, standalone components, signals, reactive forms |

Stack decisions taken during Task 1 (owner instruction): Minimal APIs instead of controllers,
SQL Server 2022 instead of PostgreSQL, and 64-bit Snowflake ids instead of GUIDs. All docs and
specs are written for that stack; there is nothing left to translate.

## Solution layout

```
src/
  Ordering.Domain/          entities, value objects, domain errors. No dependencies.
  Ordering.Application/     CQRS commands/queries + abstractions. Depends on Domain only.
  Ordering.Infrastructure/  EF Core, Dapper, outbox, worker, fake delivery. Depends on Application.
  Ordering.Api/             minimal-API endpoints, DI composition, middleware.
tests/
  Ordering.IntegrationTests/  real SQL Server, concurrency + transaction tests
  Ordering.UnitTests/         pure domain/pricing rules
web/
  order-console/            Angular app
```

Dependency rule: `Api -> Infrastructure -> Application -> Domain`. Never the reverse.
`Application` defines interfaces, `Infrastructure` implements them.

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
- Pipeline behaviours: `ValidationBehavior`, `LoggingBehavior`, `TransactionBehavior`.
- `IUnitOfWork`, `IClock`, `IIdGenerator`.
- `BaseCommandHandler<TCommand,TResult>` — hosts the transaction/`IUnitOfWork` plumbing so
  concrete handlers only express business intent.
- `BaseDapperRepository` — owns connection creation, `QuerySingleOrDefaultAsync` helpers.
- `ResultExtensions.ToHttpResult(Result)` (`Api/Endpoints`) — the single Result→HTTP mapping in the codebase.

If you are about to write the same plumbing a second time, move it into a base class first.

## Correctness rules

1. **Money is `decimal`** everywhere, `decimal(18,2)` in SQL Server. Never `float`/`double`.
2. **Never use a process-local lock** (`lock`, `SemaphoreSlim`, static dictionaries) to protect
   stock, idempotency or cancellation. The database must be correct across N instances.
3. Stock is deducted by a **conditional UPDATE** guarded by a `CHECK (available_quantity >= 0)`
   constraint. Zero rows affected = 409 conflict, not an exception path.
4. Order creation deducts stock, inserts the order and inserts the outbox message in **one
   transaction**. Partial state is a bug.
5. Idempotency is enforced by a **unique index**, not by a read-then-write check.
6. State transitions use conditional updates (`WHERE status = 'Confirmed'`) or an EF concurrency
   token. Never read → decide → write without a guard.
7. Outbox delivery is **at-least-once** with a stable event id. Document duplicate risk, do not
   pretend it is exactly-once.
8. **Every id comes from `IIdGenerator.NewId()`** (Snowflake, `bigint`, assigned before insert,
   `ValueGeneratedNever()` in EF). No `NEWID()`, no identity columns. `long` is a JSON string on
   the wire and a `string` in TypeScript — 2^53 is smaller than a Snowflake.

## Style

- KISS: the simplest thing that satisfies the requirement and its test. No mediator library,
  no repository-per-entity, no generic "framework" nobody asked for.
- DRY: shared behaviour lives in the base classes above or in `lib/` on the frontend.
- SOLID: handlers depend on interfaces from `Application`, not on EF/Dapper types.
- `nullable` enabled, warnings as errors, file-scoped namespaces, no regions.
- Public behaviour is named in domain language: `ConfirmOrder`, `RestoreStock`, not `UpdateData`.

## Commands

```bash
docker compose up -d                 # SQL Server 2022 on 1433; db-init creates the Ordering database
dotnet run --project src/Ordering.Api # API on https://localhost:5001
dotnet test                          # all tests (Testcontainers pulls mcr.microsoft.com/mssql/server:2022-latest)
dotnet ef migrations add <Name> -p src/Ordering.Infrastructure -s src/Ordering.Api
cd web/order-console && npm start    # Angular on 4200
```

## Task workflow

Tasks are `/task1` … `/task11`. Each command file tells you which spec in `docs/tasks/` to read.

Rules for every task:
1. Read `CLAUDE.md`, `docs/architecture.md` and the task spec before writing code.
2. Do not start a task while an earlier one's "Definition of done" is unmet.
3. Finish with `dotnet build` + `dotnet test` green, then tick the boxes in the task file.
4. Never weaken a test to make it pass. If a test is wrong, say so and explain why.
5. Anything you had to assume goes into `README.md` under Assumptions.

## Out of scope

No auth, no deployment, no real payment provider, no message broker. Payment and multi-worker
scaling are **design-only** sections in the design note (Task 11).
