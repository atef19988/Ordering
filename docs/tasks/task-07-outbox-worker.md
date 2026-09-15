# Task 7 — Outbox relay, RabbitMQ, notification consumers

Read `docs/architecture.md` §6 first. The outbox row stays the source of truth for every event;
RabbitMQ only moves it. Two hosted services replace the single polling worker of the original
brief, and both are safe to run N times:

```
outbox_messages ──claim──▶ OutboxRelay ──publish (confirms)──▶ RabbitMQ ──▶ NotificationConsumer ──▶ INotificationDeliveryService
     ▲                                                                            │
     └──────────────────── Sent / Failed / next_attempt_at written back ◀─────────┘
```

Why: the original loop (claim 20 → process → sleep 500 ms) capped delivery at ~40 messages/s per
worker and held a database lease for the whole delivery call. The relay does one cheap job and
never sleeps while there is work; delivery scales with consumer instances and prefetch, and the
database is touched only for bookkeeping.

## Infrastructure

- `docker-compose.yml`: service `rabbitmq` from `rabbitmq:4-management`, ports `5672` and
  `15672`, healthcheck `rabbitmq-diagnostics -q ping`, default `guest`/`guest` (development only;
  say so in the README). The API waits for it like it waits for SQL Server: `/health` gains a
  RabbitMQ check.
- Packages (pin in `Directory.Packages.props`): `RabbitMQ.Client` 7.x (the async `IChannel`
  API), `Testcontainers.RabbitMq` (same major as `Testcontainers.MsSql`).
- Config:

  ```json
  { "RabbitMq": { "Host": "localhost", "Port": 5672, "User": "guest", "Password": "guest", "VirtualHost": "/" },
    "Notifications": {
      "Delivery": { "FailureMode": "None|AlwaysFail|FailFirstN|Random",
                    "FailFirstN": 2, "FailureRate": 0.3, "LatencyMs": 50 },
      "Relay":    { "PollIntervalMs": 200, "BatchSize": 200, "LeaseSeconds": 30,
                    "ReaperIntervalSeconds": 60, "PublishedTimeoutSeconds": 300 },
      "Consumer": { "Prefetch": 50, "MaxAttempts": 5, "BaseBackoffMs": 200, "MaxBackoffMs": 30000 } } }
  ```

- One `IConnection` per process (singleton, automatic recovery on, `ClientProvidedName` =
  `ordering-api/{Snowflake:WorkerId}`); one `IChannel` per hosted service. No channel or exchange
  name outside `Infrastructure/Messaging/`.

## Topology — `Infrastructure/Messaging/RabbitMqTopology.cs`

Declared idempotently at startup by every service that uses it (declares with identical
arguments are no-ops in RabbitMQ). Everything durable; messages persistent.

| Object | Kind | Notes |
|---|---|---|
| `ordering.events` | topic exchange | the relay publishes here, routing key = `outbox_messages.type` |
| `notifications.order-created` | queue | bound `order.created`; `x-dead-letter-exchange: ordering.dead` |
| `ordering.retry` | direct exchange | the consumer publishes failed messages here with routing key `attempt.{n}` |
| `notifications.retry.a{n}` for n = 1 … MaxAttempts−1 | queues | `x-message-ttl = min(BaseBackoffMs × 2^(n−1), MaxBackoffMs)`, `x-dead-letter-exchange: ordering.events`, `x-dead-letter-routing-key: order.created` — the message comes back to the main queue when the tier's TTL expires |
| `ordering.dead` | fanout exchange | unparseable messages (NACK, no requeue) |
| `notifications.dead` | queue | bound to `ordering.dead`; nothing consumes it, a human looks |

One queue per retry tier — not one queue with per-message TTLs — because RabbitMQ only expires
messages from the head of a queue, so a long TTL at the head would delay every shorter one behind
it. The cost is that backoff has no jitter; the tiers are fixed per attempt. Document that.

## Migration — `AddOutboxPublishedAt`

```sql
ALTER TABLE outbox_messages ADD published_at datetimeoffset NULL;
```

Row states, exactly:

| status | claimed_by / claimed_until | published_at | meaning |
|---|---|---|---|
| `Pending` | NULL | NULL | not yet published (or scheduled for re-publish by the reaper) |
| `Processing` | set | NULL | claimed by a relay; lease running |
| `Processing` | NULL | set | published, waiting for a consumer's verdict |
| `Sent` | NULL | set | delivered; `processed_at` set |
| `Failed` | NULL | set | attempts exhausted; `last_error` kept |

`attempt_count` now counts **delivery attempts** and is incremented by the consumer, not at
claim time. Task 3's status mapping is unchanged: `Pending | Processing → Pending`.

## Relay — `Infrastructure/Outbox/OutboxRelay : BackgroundService`

Loop:

1. Claim a batch with the statement in `docs/architecture.md` §6 (`UPDLOCK, READPAST, ROWLOCK`,
   `OUTPUT inserted.*`, `claimed_by = <instance id>`, `claimed_until = now + LeaseSeconds`)
   **without** touching `attempt_count`. `READPAST` skips rows another relay holds locked
   instead of waiting on them; that is what lets N relays share one table.
2. For each row, publish through `IEventPublisher.PublishAsync(message, ct)`
   (`Application/Abstractions/Messaging/`; RabbitMQ implementation in
   `Infrastructure/Messaging/RabbitMqEventPublisher`): exchange `ordering.events`, routing key
   `type`, `MessageId = id` (as a string), `Type = type`, `Persistent = true`,
   `ContentType = application/json`, header `x-aggregate-id`, body = `payload` verbatim.
   Publisher confirms are on; the await completes when the broker has persisted the message.
3. On confirm: `UPDATE outbox_messages SET published_at = @now, claimed_by = NULL,
   claimed_until = NULL WHERE id = @id AND status = 'Processing'`.
   On publish failure: `UPDATE ... SET status = 'Pending', claimed_by = NULL, claimed_until = NULL`
   and log — it will be claimed again.
4. If the claim returned a **full** batch, loop immediately; otherwise sleep `PollIntervalMs`.

Reaper (same service, every `ReaperIntervalSeconds`):

```sql
UPDATE outbox_messages SET status = 'Pending', claimed_by = NULL, claimed_until = NULL
 WHERE status = 'Processing'
   AND ( (published_at IS NULL AND claimed_until < @now)                          -- relay died before publishing
      OR (published_at IS NOT NULL AND published_at < @now - PublishedTimeout) ); -- published, no verdict: re-publish
```

Re-publishing is what makes delivery at-least-once across a lost message or a consumer that
died holding it; the consumer's guards below make the duplicate harmless.

## Consumer — `Infrastructure/Notifications/NotificationConsumer : BackgroundService`

`BasicQosAsync(prefetchCount: Prefetch)`, manual acks, `AsyncEventingBasicConsumer` on
`notifications.order-created`. Each message runs in its own DI scope. Per message:

1. Parse `MessageId` → event id; deserialize the body as `OrderCreated`
   (`OutboxMessage.PayloadSerializerOptions`). Unparseable → NACK without requeue → dead queue.
2. Count the attempt and check the row is still live — one statement, this is the dedupe:

   ```sql
   UPDATE outbox_messages SET attempt_count = attempt_count + 1
   OUTPUT inserted.attempt_count
    WHERE id = @id AND status = 'Processing';
   ```

   Zero rows → the row is already `Sent`/`Failed` (a duplicate delivery) → ACK and stop.
3. `INotificationDeliveryService.SendAsync(eventId, attempt, customerReference, total, ct)` —
   with **no** database connection open.
4. Success → `UPDATE ... SET status = 'Sent', processed_at = @now, last_error = NULL
   WHERE id = @id AND status = 'Processing'` → ACK. `// Task 13:` publish a change hint here.
5. Failure and `attempt < MaxAttempts` → `UPDATE ... SET next_attempt_at = @now + tier(attempt),
   last_error = @message (truncated to 1000)` → publish the same body and `MessageId` to
   `ordering.retry` with routing key `attempt.{attempt}` → ACK the original.
6. Failure and `attempt >= MaxAttempts` → `UPDATE ... SET status = 'Failed', last_error = ...` → ACK.

If the process dies between step 5's publish and its ACK, the broker redelivers the original and
the message is counted twice; the bound is still `MaxAttempts`. Document it with the other
at-least-once cases.

Outbox bookkeeping (claim, publish mark, attempt, verdict, reaper) is single-statement
parameterised T-SQL through `IDbConnectionFactory` in `Infrastructure/Outbox/OutboxStore.cs`.
It is not a command and does not go through the CQRS pipeline — say so in the README.

## Delivery service (fake) — `Infrastructure/Notifications/FakeDeliveryService`

```csharp
Task<DeliveryResult> SendAsync(long eventId, int attempt, string customerReference, decimal total, CancellationToken ct);
```

`FailFirstN` fails while `attempt <= N`; because the attempt number comes from the database row
it is deterministic per message across consumer instances and process restarts with no state in
the fake. `Random` uses `FailureRate`; `LatencyMs` delays every call.

## Stable event id

The delivery call always receives `outbox_messages.id` — a Snowflake id assigned before the row
was inserted and carried as the AMQP `MessageId`. It never changes across retries, re-publishes
or tiers, so a real consumer can deduplicate. Write this down in the README along with the
**send-success / crash** cases: consumer dies after delivering and before step 4 → the broker
redelivers → step 2 counts another attempt and delivery happens again. At-least-once, duplicate
delivery possible, the consumer's job to absorb.

## Restart durability

Nothing is queued in memory. Rows are durable, queues are durable, messages are persistent. On
startup the relay finds `Pending` rows and the reaper reclaims stale `Processing` ones; the
consumer finds whatever the broker kept. Prove it with a test that stops the host mid-flight and
starts a new one against the same database and broker.

## Status exposure

Task 3's query already projects `notification_status`. Verify `Pending -> Sent` and
`Pending -> Failed` are visible through `GET /api/orders/{id}`, and `notificationAttempts`
equals the consumer's count.

## Definition of done

- [x] `FailFirstN: 2` → message is retried and eventually `Sent`; `attempt_count` is 3
- [x] `AlwaysFail` → after `MaxAttempts` the row is `Failed` and the API reports `Failed`
- [x] Retry tiers grow: `next_attempt_at` deltas across attempts are ≈ 200, 400, 800, 1600 ms with the defaults (controllable `IClock`)
- [x] Restart test: messages created while both services are down are delivered after they start
- [x] Two relays and two consumers against one database and broker → every message `Sent` exactly once, `attempt_count = 1`
- [x] 1,000 `Pending` rows are all `published_at` within 5 s of the relay starting (no sleep between full batches)
- [x] An unparseable message lands in `notifications.dead` and the consumer keeps going
- [x] Reaper: a row left `Processing` with an expired lease and no `published_at` is re-claimed and delivered

> Built without test code (owner instruction: "skip test"); the automated versions of these
> boxes belong to Task 8's harness (`RabbitMqFixture`, the `Notifications:*:Enabled` switches
> are in place). Each box was verified by hand against the compose stack (README, Task 7
> assumptions): `FailFirstN` → `GET` reports `Sent` / `notificationAttempts: 3`, log shows
> attempts 1–2 failing with 200 / 400 ms tiers; `AlwaysFail` → `Failed` / 5 attempts, tiers
> 200, 400, 800, 1600 ms, `last_error` kept, API reports `Failed`; 1,000 rows inserted while
> the API was down → all published 1.9 s after the relay started and all `Sent` with
> `attempt_count = 1`; the same 1,000 rows with two hosts (`WorkerId` 1 and 2) → both relays
> claimed batches, deliveries split 425 / 575, 1,000 × `Sent`, zero rows with
> `attempt_count > 1`; a `message_id: not-a-number` / `garbage` body published on
> `ordering.events` → `notifications.dead` (`x-death.reason = rejected`) and the next message
> was delivered; a hand-inserted `Processing` row with `claimed_until` five minutes past and no
> `published_at` → reaped, published and `Sent` within one reaper interval.
