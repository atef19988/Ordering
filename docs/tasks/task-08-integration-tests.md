# Task 8 — Required automated tests on a real database

EF Core in-memory is **not acceptable** for any of these. Use Testcontainers.

## Harness — `tests/Ordering.IntegrationTests/`

- `SqlServerFixture : IAsyncLifetime` starts `mcr.microsoft.com/mssql/server:2022-latest` via
  `Testcontainers.MsSql` once per collection, applies migrations, exposes the connection string.
- `OrderingApiFactory : WebApplicationFactory<Program>` overrides the connection string, gives
  each host its own `Snowflake:WorkerId`, and lets each test switch `Notifications` config and
  disable the background worker when it would interfere.
- `ResetAsync()` between tests: `DELETE FROM order_lines; DELETE FROM idempotency_keys;
  DELETE FROM outbox_messages; DELETE FROM orders;` (FK order — SQL Server refuses `TRUNCATE` on
  a table referenced by a foreign key) then re-seed products. Never rely on test ordering.
- Concurrency helper: `await Task.WhenAll(...)` over N real `HttpClient` calls released together
  by a `TaskCompletionSource` barrier — not a `Parallel.For` that trickles.

## The six required tests

1. **Two different orders, one unit of stock, concurrent.**
   Exactly one `201`, exactly one `409` with `stock.insufficient`, final
   `available_quantity = 0`. Assert against the database, not the response.

2. **Same order submitted concurrently with the same key.**
   N = 10+ parallel requests. Assert: one row in `orders`, `available_quantity` reduced by the
   line quantity exactly once, exactly one `order.created` row in `outbox_messages`, and every
   response carries the same order id.

3. **Key reuse with a changed quantity.**
   Successful submit, then same key with `quantity + 1` → `409 idempotency.key_reuse`.
   Snapshot row counts of `orders`, `order_lines`, `outbox_messages` and product quantity before
   and after; assert they are identical.

4. **Concurrent double cancel.**
   Two simultaneous cancels of one confirmed order. Final status `Cancelled`; product quantity
   equals its pre-order value exactly (assert the number, not just "increased").

5. **Failure before commit.**
   Inject a fault after stock deduction and before commit (a test-only
   `ICommitInterceptor`/`IFailurePoint` registered in DI, or a deliberate constraint violation on
   the outbox insert). Assert: no order, no order lines, no outbox row, stock unchanged.
   Do **not** test this with a mock — it must be a real rolled-back transaction.

6. **Delivery fails then succeeds; and exhausts.**
   a) `FailFirstN: 2` → poll `GET /api/orders/{id}` until `notificationStatus = "Sent"`,
      assert `attempt_count = 3`.
   b) `AlwaysFail` with `MaxAttempts: 3` → status becomes `Failed` and stops being retried
      (attempt count stable after a further wait).

## Conventions

- Arrange-Act-Assert, one behaviour per test, names like
  `CreateOrder_WhenTwoOrdersRaceForLastUnit_OnlyOneSucceeds`.
- Poll with a bounded `WaitFor(predicate, timeout: 10s)` helper — never `Thread.Sleep` alone.
- Assertions about invariants read the database directly with Dapper.
- Order ids in responses are JSON strings; parse with `long.Parse` before comparing to the database.

## Definition of done

- [ ] All six tests exist, are green, and fail if their safeguard is removed
      (temporarily delete the `AND available_quantity >= @qty` guard and confirm test 1 goes red —
      then restore it and note it in the README)
- [ ] `dotnet test` green from a clean clone with only Docker running
- [ ] No test uses `UseInMemoryDatabase`
