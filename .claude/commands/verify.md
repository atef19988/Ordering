---
description: Audit the code against the brief's hard requirements
---

Audit the repository against @CLAUDE.md and @docs/architecture.md. Report pass/fail with file and
line evidence for each:

1. Money is `decimal` / `decimal(18,2)` end to end — no float or double
2. No `lock`, `SemaphoreSlim`, static dictionary or other process-local guard on stock,
   idempotency or cancellation
3. Stock deduction is a conditional UPDATE and `CHECK (available_quantity >= 0)` exists
4. Order create, stock deduction, idempotency row and outbox insert share one transaction
5. Idempotency is enforced by a unique index; payload hash is canonical and documented
6. Cancellation uses a guarded status transition, restores stock once
7. Outbox claiming uses `WITH (UPDLOCK, READPAST, ROWLOCK)` with a lease; retries are bounded with backoff
8. Event id is stable across retries and passed to the delivery service
9. No integration test uses `UseInMemoryDatabase`; all six required tests exist
10. Reads use Dapper, writes use EF Core, and neither crosses over
11. `Domain` has no dependencies; `Application` has no EF/SqlClient/Dapper reference
13. Every id comes from `IIdGenerator` (Snowflake `bigint`); no `NEWID()`/identity keys; `long` is a JSON string on the wire
12. Angular: no direct `HttpClient` outside `BaseApiService`; idempotency key policy implemented

For each failure, give the smallest fix. Do not fix anything yet — report first.
