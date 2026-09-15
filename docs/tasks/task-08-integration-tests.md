# Task 8 — Required automated tests on a real database

EF Core in-memory is **not acceptable** for any of these. Use Testcontainers for SQL Server and
for RabbitMQ; the Task 13 fixture adds Redis on top of the same pattern.

## Harness — `tests/Ordering.IntegrationTests/`

- `SqlServerFixture : IAsyncLifetime` starts `mcr.microsoft.com/mssql/server:2022-latest` via
  `Testcontainers.MsSql` once per collection, applies migrations, exposes the connection string
  (exists since Task 2; extend, do not duplicate).
- `RabbitMqFixture : IAsyncLifetime` starts `rabbitmq:4-management` via `Testcontainers.RabbitMq`
  once per collection and exposes host/port/credentials. Both fixtures live in one
  `[Collection("Backend")]` so a test class gets both from a single `BackendFixture`.
- `OrderingApiFactory : WebApplicationFactory<Program>` overrides `ConnectionStrings:Ordering`
  and `RabbitMq:*`, gives each host its own `Snowflake:WorkerId`, and lets each test switch
  `Notifications:*` config and disable the relay/consumer when they would interfere
  (`Notifications:Relay:Enabled`, `Notifications:Consumer:Enabled` — add the switches).
- `ResetAsync()` between tests: `DELETE FROM order_lines; DELETE FROM idempotency_keys;
  DELETE FROM outbox_messages; DELETE FROM orders;` (FK order — SQL Server refuses `TRUNCATE` on
  a table referenced by a foreign key), re-seed products, then purge every queue in
  `RabbitMqTopology` (`QueuePurgeAsync`). Never rely on test ordering.
- Concurrency helper: `await Task.WhenAll(...)` over N real `HttpClient` calls released together
  by a `TaskCompletionSource` barrier — not a `Parallel.For` that trickles.
- `WaitFor(predicate, timeout: 10s)` polls with a bounded delay — never `Thread.Sleep` alone.

## The six required tests

1. **Two different orders, one unit of stock, concurrent.**
   Exactly one `201`, exactly one `409` with `stock.insufficient`, final
   `available_quantity = 0`. Assert against the database, not the response.

2. **Same order submitted concurrently with the same key.**
   N = 10+ parallel requests. Assert: one row in `orders`, `available_quantity` reduced by the
   line quantity exactly once, exactly one `order.created` row in `outbox_messages`, and every
   response carries the same order id. Response codes: one `201`, the rest `200` (or
   `409 idempotency.in_progress` if a waiter hit the 3 s lock timeout — assert none did).

3. **Key reuse with a changed quantity.**
   Successful submit, then same key with `quantity + 1` → `409 idempotency.key_reuse`.
   Snapshot row counts of `orders`, `order_lines`, `outbox_messages` and product quantity before
   and after; assert they are identical.

4. **Concurrent double cancel.**
   Two simultaneous cancels of one confirmed order. Final status `Cancelled`; product quantity
   equals its pre-order value exactly (assert the number, not just "increased").

5. **Failure before commit.**
   Inject a fault after the stock update and before commit (a test-only `IFailurePoint`
   registered in DI and consulted by `TransactionBehavior` between `next()` and `CommitAsync`).
   Assert: no order, no order lines, no outbox row, no idempotency row, stock unchanged.
   Do **not** test this with a mock — it must be a real rolled-back transaction.

6. **Delivery fails then succeeds; and exhausts.** (through the relay, the broker and the consumer)
   a) `FailFirstN: 2` → poll `GET /api/orders/{id}` until `notificationStatus = "Sent"`,
      assert `notificationAttempts = 3` and `outbox_messages.attempt_count = 3`.
   b) `AlwaysFail` with `MaxAttempts: 3` → status becomes `Failed` and stops being retried
      (attempt count stable after a further wait; nothing left in any `notifications.retry.*` queue).

## Tests deferred here from Tasks 3 and 4

Tasks 3 and 4 were built without test code (owner instruction). Their unticked boxes are owed by
this task, in the same harness:

- seeded products come back with exact decimal prices (`12.50`, not `12.5000001`)
- unknown order id → `404` ProblemDetails with `code: order.not_found`
- forced exception before commit → no order, no stock change, no outbox row (this is test 5)
- 409 on a multi-line order leaves every product untouched (not partially deducted)

Tick those boxes in the Task 3 and Task 4 files when they pass.

## Conventions

- Arrange-Act-Assert, one behaviour per test, names like
  `CreateOrder_WhenTwoOrdersRaceForLastUnit_OnlyOneSucceeds`.
- Assertions about invariants read the database directly with Dapper.
- Order ids in responses are JSON strings; parse with `long.Parse` before comparing to the database.
- A test that needs the broker says so in its name suffix (`_ThroughBroker`) so a reader knows
  which tests are slow.

## Definition of done

- [ ] All six tests exist, are green, and fail if their safeguard is removed
      (temporarily delete the `AND available_quantity >= @qty` guard and confirm test 1 goes red;
      temporarily drop the `AND status = 'Processing'` guard in the consumer and confirm 6a goes
      red — then restore both and note it in the README)
- [ ] The four deferred Task 3/4 tests are green and their boxes are ticked
- [ ] `dotnet test` green from a clean clone with only Docker running; total wall time recorded in the README
- [ ] No test uses `UseInMemoryDatabase`
