# Task map

Run them in the **run order** below. Each one ends green (`dotnet build` + `dotnet test`) before
the next starts. Task numbers are stable names (`/task7` always means the outbox), not the
sequence: Tasks 12–14 were added after Task 4 to make the service absorb load, and they run
before the frontend and the docs.

| Run | # | Command | Task | Depends on |
|---|---|---|---|---|
| 1 | 1 | `/task1` | Solution, layers, shared abstractions | — |
| 2 | 2 | `/task2` | Domain + EF Core write model, constraints, seed | 1 |
| 3 | 3 | `/task3` | Dapper read side (`GET /api/products`, `GET /api/orders/{id}`) | 2 |
| 4 | 4 | `/task4` | `POST /api/orders` — atomic stock deduction + outbox | 2 |
| 5 | 5 | `/task5` | Idempotency lifecycle; key row written before stock, stock last | 4 |
| 6 | 6 | `/task6` | `POST /api/orders/{id}/cancel` | 4 |
| 7 | 7 | `/task7` | Outbox relay → RabbitMQ → notification consumers | 4 |
| 8 | 8 | `/task8` | Required automated tests on real SQL Server + RabbitMQ | 3–7 |
| 9 | 12 | `/task12` | Write path: measure, then RCSI, fast-fail read, one-round-trip deduct | 8 |
| 10 | 13 | `/task13` | Read path: output cache + SSE push over a RabbitMQ fan-out | 7, 8 |
| 11 | 14 | `/task14` | Admission control: concurrency limits, sold-out fast-fail, 503s | 12, 13 |
| 12 | 9 | `/task9` | Angular foundation: design system + `lib/` base services | — |
| 13 | 10 | `/task10` | Angular order feature: form, lookup, cancel, live status | 9, 3–6, 13, 14 |
| 14 | 11 | `/task11` | README, capacity sign-off, one-page design note | all |

Tasks 1–8 are the brief's backend, 12–14 the scaling work, 9–10 frontend, 11 documentation.
9 can start any time.

## Scope decisions behind Tasks 12–14 (owner instructions, after Task 4)

- **RabbitMQ, not Kafka.** One broker (`rabbitmq:4-management` in compose), a topic exchange,
  durable queues, persistent messages, publisher confirms. RabbitMQ *moves messages*: outbox
  events to the notification consumers, and best-effort change hints to the API instances.
- **Redis, but never as the truth.** Redis *holds hot state*: the shared output cache (Task 13),
  the stock admission gate and the idempotency fast path (Task 14). It can answer 409 or a 200
  replay on its own; it can never answer 201 — that still needs the conditional `UPDATE` and the
  unique index in SQL Server. Redis down means gate open and cache miss, not an outage.
- **No sharding.** One SQL Server stays the only source of truth for stock and orders, so the
  brief's rule that *the database* enforces stock is untouched. Moving the counter itself into
  Redis, or stock buckets, is the documented next lever (Task 11 design note §5), not built.
- **The API contract does not change.** `POST /api/orders` still answers 201/409 synchronously;
  there is no 202/asynchronous intake, because the brief and Task 8's tests require the 409 to be
  the response. Throughput comes from a shorter lock window, cached reads, push instead of
  polling, and rejecting early when full — each change measured before and after
  (`docs/capacity.md`).

## Requirement traceability

| Brief requirement | Task |
|---|---|
| Seed two products, decimal money | 2 |
| `POST /api/orders`, server-side totals | 4 |
| `GET /api/orders/{id}` with notification status | 3, 7 |
| `POST /api/orders/{id}/cancel` exactly-once restore | 6 |
| `GET /api/products` | 3 |
| Atomic create, 409 on insufficient stock | 4 |
| No process-local lock, DB enforces | 2, 4, 14 |
| Same key + same payload → original order | 5 |
| Same key + different payload → 409 | 5 |
| Concurrent same key → one order, one deduction | 5, 8 |
| Concurrent cancel → one restore | 6, 8 |
| Worker, fake delivery, configurable failure | 7 |
| Bounded retries + backoff + Failed status | 7 |
| Survives restart, stable event id | 7 |
| Angular form / lookup / cancel / status | 10 |
| Six required tests on a real database | 8 |
| Design note, README, AI disclosure | 11 |

| Scaling requirement (owner, after Task 4) | Task |
|---|---|
| Notification delivery scales horizontally, no polling cap | 7 |
| Reads never block behind writers; catalogue served from cache | 12, 13 |
| Status is pushed, not polled | 13, 10 |
| Bursts are answered with 503 + `Retry-After`, never 500 or a pool timeout | 14 |
| Capacity is measured and written down, before and after each change | 12, 11 |
