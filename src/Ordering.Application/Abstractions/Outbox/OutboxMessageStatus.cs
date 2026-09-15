namespace Ordering.Application.Abstractions.Outbox;

/// <summary>Stored as its name in <c>outbox_messages.status</c>; the worker's SQL uses the same literals.</summary>
public enum OutboxMessageStatus
{
    Pending,
    Processing,
    Sent,
    Failed,
}
