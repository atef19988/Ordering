---
description: Audit the code against the brief's hard requirements
---

Audit the repository against @CLAUDE.md and @docs/architecture.md. Report pass/fail with file and
line evidence for each:

1. Money is `decimal` / `decimal(18,2)` end to end — no float or double
2. No `lock`, `SemaphoreSlim`, static dictionary or other process-local guard on stock,
   idempotency or cancellation
3. Stock deduction is a conditional UPDATE and `CHECK (available_quantity >= 0)` exists
4. Order create, stock deduction, idempotency row and outbox insert share one transaction, and
   the stock UPDATE is the last statement before commit
5. Idempotency is enforced by a unique index; payload hash is canonical and documented
6. Cancellation uses a guarded status transition, restores stock once, last before commit
7. Outbox relay claims with `WITH (UPDLOCK, READPAST, ROWLOCK)` and a lease, publishes with
   confirms, never sleeps on a full batch; the reaper exists
8. Every consumer write is guarded by `status = 'Processing'`; retries are bounded with tiered
   backoff; the AMQP `MessageId` is the Snowflake event id and is passed to the delivery service
9. No integration test uses `UseInMemoryDatabase`; all six required tests exist
10. Reads use Dapper, writes use EF Core, and neither crosses over (outbox bookkeeping SQL is
    the documented exception)
11. `Domain` has no dependencies; `Application` has no EF/SqlClient/Dapper/RabbitMQ/Redis reference
12. Every id comes from `IIdGenerator` (Snowflake `bigint`); no `NEWID()`/identity keys; `long` is a JSON string on the wire
13. `READ_COMMITTED_SNAPSHOT` is turned on by a migration with `suppressTransaction: true`
14. No RabbitMQ or Redis call inside a transaction; `IUnitOfWork.OnCommitted` is the only
    post-commit I/O
15. Every code path that returns 201 ran the conditional UPDATE with one row affected; the Redis
    gate and idempotency cache can only produce 409 or a 200 replay, and fail open
16. The write concurrency limit is below `Max Pool Size`; rejections are 503 with `Retry-After`;
    no request can wait on the connection pool
17. Every tuning change in Tasks 12–14 has a before/after row in `docs/capacity.md`
18. Angular: no direct `HttpClient` or `EventSource` outside `lib/`; idempotency key policy
    implemented; every 503 retries with the same key

For each failure, give the smallest fix. Do not fix anything yet — report first.
