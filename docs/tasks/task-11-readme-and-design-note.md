# Task 11 — README and one-page design note

Both are graded artifacts. Write them from the finished code, not from intentions.

## `README.md`

1. **Setup** — prerequisites, `docker compose up -d` (SQL Server 2022 + `db-init`), connection string,
   `Snowflake:WorkerId` per instance, `dotnet ef database update`.
2. **Seed** — the exact command and the two seeded products.
3. **Run** — API, Angular, and which ports.
4. **Test** — `dotnet test`, what Docker is needed for, roughly how long it takes.
5. **API reference** — the four endpoints with a sample request/response each, including the 409 bodies.
6. **Payload equivalence** — the canonical-form definition from Task 5, verbatim, plus one
   example of two requests that are equivalent and one pair that is not.
7. **Duplicate in flight** — the exact response a caller gets while its duplicate is still
   processing, and why (`docs/architecture.md` §4).
8. **Duplicate delivery** — send-success / worker-crash, at-least-once, stable event id.
9. **Assumptions** — every decision you made that the brief left open. At minimum: duplicate
   product codes in one request rejected, replay returns current state, cancel does not suppress
   a pending created-notification, case-sensitive customer reference in the hash, `FailFirstN`
   keyed by event id.
10. **Time spent** — honest per-area breakdown.
11. **Unfinished work** — what you would do next and why it was cut.
12. **AI use disclosure** — which tools, for what (scaffolding, tests, docs), and a statement
    that you can explain every line. Required by the brief; do not skip it.

## `docs/design-note.md` — one page, hard limit

Four sections, tight prose, no filler:

1. **Transaction boundary, concurrency, constraints, idempotency lifecycle.**
   Condense `docs/architecture.md` §1–§4. Say *why* the conditional UPDATE beats
   `SELECT FOR UPDATE` and why the key row lives inside the transaction.

2. **Running multiple notification workers (design only).**
   Work claiming with `UPDLOCK, READPAST` and a lease; crashed worker → lease expiry →
   reclaim; duplicate delivery is possible, consumers dedupe on the stable event id; ordering is
   per-aggregate at best; a reaper for rows stuck in `Processing`.

3. **Real payment provider without holding a transaction open (design only).**
   `AwaitingPayment` order + `payment_intents` row + outbox, committed before any network call.
   Worker calls the provider with the intent id as the provider-side idempotency key. Payment
   succeeds then the app dies → the intent is still `Submitted`; a reconciliation job re-queries
   the provider with the same key and converges to `Paid`, or the provider's webhook does it
   first — both paths are idempotent, so no double charge and no lost payment.

4. **Minimum logs and metrics.**
   Logs (structured, with `OrderId`, `IdempotencyKey`, `EventId`, `CorrelationId`):
   stock conflict, key reuse, duplicate replay, outbox attempt failure, attempts exhausted.
   Metrics: `orders_created_total`, `stock_conflicts_total{product}`,
   `idempotent_replays_total`, `idempotency_conflicts_total`,
   `outbox_pending_age_seconds` (max age of a pending row — the stuck-notification alarm),
   `outbox_attempts_total{result}`, `outbox_failed_total`, order-create latency histogram.
   Alarms: pending age > 5 min, failed count > 0, stock conflict rate spike.

## Definition of done

- [ ] `docs/design-note.md` fits on one page when printed
- [ ] README commands work from a clean clone — verify by actually following them
- [ ] Every "Assumption" in the README matches actual behaviour in the code
