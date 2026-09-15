# Task map

Run them in order. Each one ends green (`dotnet build` + `dotnet test`) before the next starts.

| # | Command | Task | Depends on |
|---|---|---|---|
| 1 | `/task1` | Solution, layers, shared abstractions | — |
| 2 | `/task2` | Domain + EF Core write model, constraints, seed | 1 |
| 3 | `/task3` | Dapper read side (`GET /api/products`, `GET /api/orders/{id}`) | 2 |
| 4 | `/task4` | `POST /api/orders` — atomic stock deduction + outbox | 2 |
| 5 | `/task5` | Idempotency lifecycle | 4 |
| 6 | `/task6` | `POST /api/orders/{id}/cancel` | 4 |
| 7 | `/task7` | Outbox dispatcher + notification worker | 4 |
| 8 | `/task8` | Required automated tests on real SQL Server | 3–7 |
| 9 | `/task9` | Angular foundation: design system + `lib/` base services | — |
| 10 | `/task10` | Angular order feature: form, lookup, cancel, status | 9, 3–6 |
| 11 | `/task11` | README + one-page design note | all |

Tasks 1–8 are backend, 9–10 frontend, 11 documentation. 9 can start any time.

## Requirement traceability

| Brief requirement | Task |
|---|---|
| Seed two products, decimal money | 2 |
| `POST /api/orders`, server-side totals | 4 |
| `GET /api/orders/{id}` with notification status | 3, 7 |
| `POST /api/orders/{id}/cancel` exactly-once restore | 6 |
| `GET /api/products` | 3 |
| Atomic create, 409 on insufficient stock | 4 |
| No process-local lock, DB enforces | 2, 4 |
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
