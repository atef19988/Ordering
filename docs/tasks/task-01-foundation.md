# Task 1 — Solution, layers and shared abstractions

Build the skeleton every later task plugs into. No business logic here.

## Deliverables

1. `Ordering.sln` with the four `src` projects and two `tests` projects from `CLAUDE.md`,
   plus project references that enforce `Api -> Infrastructure -> Application -> Domain`.
2. `Directory.Build.props`: `net9.0`, `nullable enable`, `TreatWarningsAsErrors`,
   `ImplicitUsings enable`, `LangVersion latest`.
3. `docker-compose.yml` with SQL Server 2022, a named volume, and a one-shot `db-init` job that
   creates the `Ordering` database (SQL Server has no `POSTGRES_DB`-style auto-create).

## Abstractions — `Ordering.Application/Abstractions/`

```
Result.cs              Result, Result<T>, implicit success conversion
Error.cs               record Error(string Code, string Message, ErrorType Type)
                       ErrorType: Validation | NotFound | Conflict | Failure
Messaging/ICommand.cs  ICommand, ICommand<T>, IQuery<T>
Messaging/IHandler.cs  ICommandHandler<TCommand>, ICommandHandler<TCommand,TResult>,
                       IQueryHandler<TQuery,TResult>
Messaging/IDispatcher.cs + Dispatcher.cs   resolves handler from DI, runs behaviours
Behaviors/ValidationBehavior.cs            FluentValidation -> Result validation error
Behaviors/LoggingBehavior.cs               request name, duration, result code
Behaviors/TransactionBehavior.cs           opens tx for ICommand only, commits on success
BaseCommandHandler.cs                      protected IUnitOfWork, IClock; template method
IUnitOfWork.cs  IClock.cs  IIdGenerator.cs  (IIdGenerator.NewId() returns a Snowflake long)
```

Do **not** add MediatR. The dispatcher is ~40 lines of DI resolution; that is the KISS choice
and it keeps `Application` free of third-party message contracts.

`BaseCommandHandler<TCommand,TResult>` exposes `Task<Result<TResult>> HandleCore(...)` and owns
nothing else. If a base class starts collecting `if`s about specific commands, it is wrong.

## API base — `Ordering.Api/Endpoints/ResultExtensions.cs` (Minimal API)

One mapping, `ToHttpResult()`, used by every endpoint:

| `ErrorType` | HTTP |
|---|---|
| Validation | 400 + ProblemDetails with `errors` |
| NotFound | 404 |
| Conflict | 409 |
| Failure | 500 |

Every failure ProblemDetails carries the stable error code in `extensions.code`.

Also add: `ExceptionHandlingMiddleware` (unhandled → 500 ProblemDetails + logged), Serilog with
`RequestId` and `IdempotencyKey` enrichers, Swagger, a `/health` endpoint that runs `SELECT 1`
against the configured database, and `LongAsStringJsonConverter` so Snowflake ids survive
JavaScript clients (Swagger maps `long` to `string`/`int64` to match).

## Infrastructure base

- `BaseDapperRepository` — takes `IDbConnectionFactory`, exposes protected
  `QuerySingleOrDefaultAsync<T>`, `QueryAsync<T>`, `ExecuteAsync`. Subclasses only write SQL.
- `SqlConnectionFactory` (Microsoft.Data.SqlClient) implementing `IDbConnectionFactory`.
- `SystemClock`, `SnowflakeIdGenerator` (lock-free CAS; worker id from `Snowflake:WorkerId`).

## Definition of done

- [x] `dotnet build` clean with warnings as errors
- [x] `dotnet test` runs (one placeholder test proving the harness works)
- [x] Architecture test asserts `Domain` references nothing and `Application` references no EF/SqlClient/Dapper
- [x] `docker compose up -d` gives a reachable SQL Server with the `Ordering` database (`/health` → 200)
