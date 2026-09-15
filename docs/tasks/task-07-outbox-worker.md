# Task 7 — Outbox dispatcher and notification worker

## Delivery service (fake)

`Infrastructure/Notifications/FakeDeliveryService : INotificationDeliveryService`

```csharp
Task<DeliveryResult> SendAsync(long eventId, string customerReference, decimal total, CancellationToken ct);
```

Behaviour driven by `Notifications:Delivery` config so tests can steer it:

```json
{ "Notifications": {
    "Delivery": { "FailureMode": "None|AlwaysFail|FailFirstN|Random",
                  "FailFirstN": 2, "FailureRate": 0.3, "LatencyMs": 50 },
    "Worker":   { "PollIntervalMs": 500, "BatchSize": 20, "MaxAttempts": 5,
                  "BaseBackoffMs": 200, "MaxBackoffMs": 30000, "LeaseSeconds": 30 } } }
```

`FailFirstN` must key off the **event id** so "fail twice then succeed" is deterministic per
message across process restarts (keep a counter table or an injectable in-memory store used only
by tests — state this in the README).

## Worker — `Infrastructure/Outbox/OutboxProcessor : BackgroundService`

Loop: claim a batch → process each → sleep `PollIntervalMs`.

Claim (single statement, see `docs/architecture.md` §6) using `WITH (UPDLOCK, READPAST, ROWLOCK)`
and `OUTPUT inserted.*`, a `claimed_by` worker id and a `claimed_until` lease, incrementing
`attempt_count` at claim time. `READPAST` skips rows another worker holds locked instead of
waiting on them; that is what lets N workers share one table.

Per message:
- success → `status='Sent'`, `processed_at = SYSDATETIMEOFFSET()`, `last_error = NULL`
- failure and `attempt_count < MaxAttempts` → `status='Pending'`,
  `next_attempt_at = now + min(BaseBackoff * 2^(attempt-1), MaxBackoff) + jitter`
  (computed from `IClock`, written as a `datetimeoffset` parameter),
  `last_error = <message truncated to 1000 chars>`
- failure and `attempt_count >= MaxAttempts` → `status='Failed'`, `last_error` kept

Each message is processed on its own scope/connection; one poison message never blocks the batch.

## Stable event id

The delivery call always receives `outbox_messages.id` — a Snowflake id assigned before the row
was inserted. It never changes across retries, so a real consumer can deduplicate. Write this
down in the README along with the **send-success / worker-crash** case: the lease expires, the
message is retried, delivery is at-least-once, duplicate delivery is possible and is the
consumer's job to absorb.

## Restart durability

Nothing is queued in memory. On startup the worker finds `Pending` rows plus `Processing` rows
whose lease expired and resumes. Prove it with a test that stops the host mid-flight and starts
a new one against the same database.

## Status exposure

Task 3's query already projects `notification_status`. Verify `Pending -> Sent` and
`Pending -> Failed` are visible through `GET /api/orders/{id}`.

## Definition of done

- [ ] `FailFirstN: 2` → message is retried and eventually `Sent`; attempt count is 3
- [ ] `AlwaysFail` → after `MaxAttempts` the row is `Failed` and the API reports `Failed`
- [ ] Backoff delays actually grow (assert `next_attempt_at` deltas, use a controllable `IClock`)
- [ ] Restart test: messages created while the worker is down are delivered after it starts
- [ ] Two worker instances against one database deliver each message without both claiming it
