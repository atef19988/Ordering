using System.Text.Json;
using Ordering.Application.Abstractions.Serialization;

namespace Ordering.Application.Abstractions.Outbox;

/// <summary>
/// One row of <c>outbox_messages</c>: an integration event written in the same transaction as the
/// change it announces. <see cref="Id"/> is a Snowflake assigned before insert, so it is the stable
/// event id a consumer can deduplicate on across retries. This type only creates the pending row;
/// claiming, attempts and backoff are the worker's business (Task 7) and happen in SQL.
/// </summary>
public sealed class OutboxMessage
{
    public const int TypeMaxLength = 64;
    public const int StatusMaxLength = 16;
    public const int ClaimedByMaxLength = 64;
    public const int LastErrorMaxLength = 1000;

    /// <summary>Payloads are camelCase JSON with <c>long</c>s as strings — the same dialect as the API.</summary>
    public static readonly JsonSerializerOptions PayloadSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new LongAsStringJsonConverter() },
    };

    private OutboxMessage(long id, string type, long aggregateId, string payload, DateTimeOffset occurredAt)
    {
        Id = id;
        Type = type;
        AggregateId = aggregateId;
        Payload = payload;
        OccurredAt = occurredAt;
        Status = OutboxMessageStatus.Pending;
        AttemptCount = 0;
        NextAttemptAt = occurredAt;
    }

    public long Id { get; private set; }

    /// <summary>Event name, e.g. <c>order.created</c>.</summary>
    public string Type { get; private set; }

    public long AggregateId { get; private set; }

    /// <summary>JSON document; the database enforces <c>ISJSON(payload) = 1</c>.</summary>
    public string Payload { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public OutboxMessageStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public string? ClaimedBy { get; private set; }

    public DateTimeOffset? ClaimedUntil { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public string? LastError { get; private set; }

    /// <param name="id">Snowflake id from <c>IIdGenerator</c>; becomes the event id delivered to consumers.</param>
    /// <param name="occurredAt">Also the first <see cref="NextAttemptAt"/>: the message is due immediately.</param>
    public static OutboxMessage Create<TPayload>(long id, string type, long aggregateId, TPayload payload, DateTimeOffset occurredAt)
        where TPayload : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(type.Length, TypeMaxLength, nameof(type));
        ArgumentNullException.ThrowIfNull(payload);

        return new OutboxMessage(id, type, aggregateId, JsonSerializer.Serialize(payload, PayloadSerializerOptions), occurredAt);
    }
}
