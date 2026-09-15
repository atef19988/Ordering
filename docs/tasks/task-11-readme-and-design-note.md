# Task 11 — README and one-page design note

Both are graded artifacts. Write them from the finished code and from the measured numbers in
`docs/capacity.md`, not from intentions.

## `README.md`

1. **Setup** — prerequisites, `docker compose up -d` (SQL Server 2022 + `db-init`, RabbitMQ with
   its management UI on 15672, Redis), connection strings, `Snowflake:WorkerId` per instance,
   `dotnet ef database update`.
2. **Seed** — the exact command and the two seeded products; the `--products N` load catalogue.
3. **Run** — API, Angular, and which ports; how to run two API instances locally (two ports,
   two worker ids) so the SSE-across-instances behaviour can be seen.
4. **Test** — `dotnet test`, which containers it pulls, roughly how long it takes.
5. **Load test** — the k6 commands from Task 12, the two-terminal read-under-write run, and how
   to read `waits.sql`.
6. **API reference** — the five endpoints (`GET /api/orders/{id}/events` included) with a sample
   request/response each, including the 409 and 503 bodies and the `Retry-After` header.
7. **Payload equivalence** — the canonical-form definition from Task 5, verbatim, plus one
   example of two requests that are equivalent and one pair that is not.
8. **Duplicate in flight** — the exact response a caller gets while its duplicate is still
   processing, and why (`docs/architecture.md` §4); and the Redis fast path: when a replay never
   touches SQL Server.
9. **Duplicate delivery** — every at-least-once case from Task 7 (send-success/crash, re-publish
   by the reaper, retry-publish/ACK gap), the stable event id, what a consumer must do.
10. **What Redis is allowed to decide** — the correctness argument from Task 14 verbatim: gate
    and cache may answer 409 or 200, never 201; fail-open; drift bounded by the reconciler.
11. **Capacity** — the summary table from `docs/capacity.md` (baseline → final for `hot`,
    `spread`, reads, admission), the machine it was measured on, and the one-line formula
    `orders/s per product ≈ 1000 ÷ lock-hold-ms` with the measured lock-hold.
12. **Assumptions** — every decision you made that the brief left open. At minimum: duplicate
    product codes in one request rejected, replay returns current state, cancel does not suppress
    a pending created-notification, case-sensitive customer reference in the hash, `FailFirstN`
    keyed by attempt number, retry tiers without jitter, catalogue ≤ 1 s stale, the gate's
    `available: 0`, 503 rather than 429.
13. **Time spent** — honest per-area breakdown.
14. **Unfinished work** — what you would do next and why it was cut.
15. **AI use disclosure** — which tools, for what (scaffolding, tests, docs), and a statement
    that you can explain every line. Required by the brief; do not skip it.

## `docs/design-note.md` — one page, hard limit

Five sections, tight prose, no filler:

1. **Transaction boundary, concurrency, constraints, idempotency lifecycle.**
   Condense `docs/architecture.md` §1–§4. Say *why* the conditional UPDATE beats
   `SELECT FOR UPDATE`, why the key row lives inside the transaction, and why the stock update is
   the last statement before commit.

2. **Delivery: outbox, relay, RabbitMQ, consumers.**
   Claiming with `UPDLOCK, READPAST` and a lease; publisher confirms; retry tiers; the consumer's
   `WHERE status = 'Processing'` guards; the reaper; every duplicate-delivery case in one
   sentence each; ordering is per-aggregate at best.

3. **Real payment provider without holding a transaction open (design only).**
   `AwaitingPayment` order + `payment_intents` row + outbox, committed before any network call.
   Worker calls the provider with the intent id as the provider-side idempotency key. Payment
   succeeds then the app dies → the intent is still `Submitted`; a reconciliation job re-queries
   the provider with the same key and converges to `Paid`, or the provider's webhook does it
   first — both paths are idempotent, so no double charge and no lost payment.

4. **Minimum logs and metrics.**
   Logs (structured, with `OrderId`, `IdempotencyKey`, `EventId`, `CorrelationId`):
   stock conflict (with `source`), key reuse, duplicate replay (with `source`), gate open,
   admission rejection, outbox attempt failure, attempts exhausted, reaper re-publish.
   Metrics: the Task 14 `Meter("Ordering")` counters, `outbox_pending_age_seconds` (max age of a
   `Pending` row — the stuck-notification alarm), `outbox_published_unacked_age_seconds`,
   `notifications_queue_depth`, `stock_gate_drift{product}` (Redis − database, from the
   reconciler), order-create latency histogram, lock-hold histogram.
   Alarms: pending age > 5 min, failed count > 0, gate drift outside ±1 % for two intervals,
   `server.busy` rate > 1 % of requests, stock conflict rate spike.

5. **Capacity and the next lever (design only).**
   The measured ceiling and where it sits (row lock vs `WRITELOG`). Then, in one paragraph
   each, what would be needed for a further 10×: moving the counter itself into Redis with the
   database as a reconciled ledger (what changes in the correctness argument, what is lost); stock
   buckets inside the one database (K rows per product, random bucket, false 409s); and why
   neither was built — the brief's rule that the database enforces stock, and the honest
   observation that below ~10k orders/s they are not worth their operational cost.

## Definition of done

- [ ] `docs/design-note.md` fits on one page when printed
- [ ] README commands work from a clean clone — verify by actually following them, including the
      load-test commands
- [ ] Every "Assumption" in the README matches actual behaviour in the code
- [ ] Every number in the README capacity section traces to a row in `docs/capacity.md`
