# Task 12 — Write path: measure, then tune

The ceiling of this system is the exclusive row lock on a product between its conditional
`UPDATE` and the commit: **orders/s per product ≈ 1000 ÷ lock-hold-ms**. Across many products the
ceiling is SQL Server's commit rate (one log flush per order). This task first measures both,
then makes the changes below one at a time, measuring after each. A change with no before/after
row in `docs/capacity.md` did not happen.

Nothing here changes an invariant. The conditional `UPDATE` and the `CHECK` constraint remain
the only things that decide stock.

## Load harness — `tools/load/`

k6, run through Docker so nobody installs anything:

```bash
dotnet run --project src/Ordering.Api -- seed --products 200    # adds LOAD-000001..LOAD-000200 (Task 15's switch; add it here if 15 has not run yet); the two brief products are untouched
docker run --rm -i -e API_BASE=http://host.docker.internal:5000 -e SCENARIO=hot grafana/k6 run - < tools/load/create-orders.js
```

Scripts (ES modules, no bundler):

| File | What it does |
|---|---|
| `create-orders.js` | `constant-arrival-rate` stages 50 → 200 → 800 → 3200 req/s, 30 s each; fresh `crypto.randomUUID()` key per request; `SCENARIO=hot` → every order is 1 × `LOAD-0001`; `SCENARIO=spread` → one random product per order; `SCENARIO=multi` → 3 random products. Thresholds: `http_req_failed{status:500} == 0`, `p(95) < 500ms` |
| `read-products.js` | `GET /api/products?search=<random 1–3 char prefix>&sort=<random>&pageSize=50`, walking `nextCursor` for 3 pages, at 2,000 req/s **while** `create-orders.js hot` runs (open two terminals; write it in the README). Task 15's paged shape — never the whole table |
| `read-order.js` | `GET /api/orders/{id}` over ids captured from a create run |
| `waits.sql` | snapshot `sys.dm_os_wait_stats` for `LCK_M_X`, `LCK_M_U`, `LCK_M_S`, `WRITELOG`, `PAGELATCH_*`; run before and after, subtract |
| `seed.js` | 200 products only — the API's `seed --products` is the supported path; this is for a broker-less DB |

The API under test runs with `ASPNETCORE_ENVIRONMENT=Production` (no seed on startup, Swagger
still on) and `Serilog` at `Warning` for `Microsoft.AspNetCore` — the same as the shipped
`appsettings.json`.

## `docs/capacity.md`

One table per scenario, one row per state of the code:

| Row | hot orders/s at p95<500 ms | spread orders/s | products p99 under write load | top wait | 500s | notes |
|---|---|---|---|---|---|---|
| Baseline (after Task 8) | | | | | | |
| + RCSI | | | | | | |
| + fast-fail read | | | | | | |
| + one-round-trip deduct | | | | | | |

Numbers come from the k6 summary and `waits.sql`; each row names the commit it was measured on
and the machine (CPU, Docker Desktop version). Estimates are not allowed in this file.

## Changes, in this order

### 1. `READ_COMMITTED_SNAPSHOT` — migration `EnableReadCommittedSnapshot`

```csharp
migrationBuilder.Sql(
    "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;",
    suppressTransaction: true);   // ALTER DATABASE cannot run inside the migration transaction
```

Testcontainers databases get it through the same migration. Effect: readers no longer block
behind an in-flight order's row lock — `GET /api/products` stays fast during a hot run.

Why the stock guard is still safe: under RCSI only *reads* see the last committed version. An
`UPDATE` locates its rows through the version store but then takes the `U`/`X` lock and
re-evaluates its `WHERE` against the **current** row, so two racing decrements still serialise on
the lock and the second still sees the first's result. The `CHECK` constraint is the backstop
either way. Put this paragraph in `docs/architecture.md` §2.

### 2. Fast-fail read before any write

In `CreateOrderCommandHandler`, after the catalogue load and before staging anything:

```sql
SELECT code, available_quantity FROM products WHERE code IN @codes;   -- IProductRepository.GetAvailableAsync
```

Any line with `available < quantity` → `409 stock.insufficient` immediately: nothing inserted,
nothing locked, nothing rolled back. Under RCSI this read takes no lock and never waits.

This is **not a guard** — the value may be stale by the time the `UPDATE` runs, which is why the
`UPDATE` still carries `AND available_quantity >= @qty`. It only removes the wasted inserts and
rollback from the obvious conflicts. Say exactly that in the code comment and the README.

### 3. One-round-trip deduct — `IStockRepository.TryDeductAllAsync(lines)`

Replace N per-line round trips with one batch that keeps the per-line statements **and their
order** (the deadlock argument from Task 4 depends on it):

```sql
DECLARE @affected int = 0;
UPDATE products SET available_quantity = available_quantity - @q0 WHERE code = @c0 AND available_quantity >= @q0; SET @affected += @@ROWCOUNT;
UPDATE products SET available_quantity = available_quantity - @q1 WHERE code = @c1 AND available_quantity >= @q1; SET @affected += @@ROWCOUNT;
SELECT @affected;
```

Built by the repository from the (already code-ordered) lines with numbered parameters — never
string-concatenated values. `@affected < lines.Count` → the handler reads which line is short
(`GetAvailableAsync` again) and returns `409 stock.insufficient` for the first one; the
transaction rolls back as before. Do not switch to a single `UPDATE … JOIN @tvp`: its lock order
follows the query plan, not the product code.

### 4. Connection pool

Add `Min Pool Size=20` to the connection string so a cold instance does not pay 20 handshakes
under the first burst. Leave `Max Pool Size` at the default 100 unless `waits.sql` or
`SqlClient` pool-wait counters show queueing — and if you raise it, raise the admission limit in
Task 14 with it, never past it.

### 5. Logging cost

`LoggingBehavior` and Serilog request logging write one line per request at `Information`.
Measure with them at `Warning`; if the difference at 800 req/s is more than 5 % throughput, keep
the per-request line at `Debug` and log only outcomes that matter (409, 503, 5xx) at
`Information`. Record the number either way.

## Not allowed

- `DELAYED_DURABILITY` — faster commits by risking confirmed orders on a crash.
- Dropping or disabling `ck_products_qty_non_negative`.
- `NOLOCK`/`READUNCOMMITTED` anywhere on the write path.
- Retrying deadlock victims or lock timeouts in a C# loop. One attempt, then the mapped
  409/503; the client retries with the same key.
- Any change to what a 201 or a 409 means.

## Definition of done

- [ ] `tools/load/` runs from a clean clone with only Docker; the README shows the exact commands
- [ ] `docs/capacity.md` has the baseline row, measured **before** any change in this task
- [ ] One row per change above, each measured on its own commit; the top wait type is named
- [ ] `GET /api/products` p99 under a `hot` run drops below 50 ms after RCSI (it blocks before)
- [ ] `hot` orders/s at p95 < 500 ms is at least 2× the baseline after changes 1–3, or the note explains what bounded it (`WRITELOG` is the honest answer on Docker Desktop)
- [ ] Zero `500`s and zero pool-timeout exceptions in any run; 409/503 only
- [ ] `/race "last unit of SKU-001"` still holds 5/5, and every Task 8 test is green
