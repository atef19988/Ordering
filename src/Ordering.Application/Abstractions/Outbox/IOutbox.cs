namespace Ordering.Application.Abstractions.Outbox;

/// <summary>
/// The transactional outbox's write side. Implemented over EF Core by
/// <c>Infrastructure.Outbox.TransactionalOutbox</c>; the worker that drains it is Task 7.
/// </summary>
public interface IOutbox
{
    /// <summary>
    /// Stages a message in the current unit of work. It is written by the same
    /// <c>SaveChanges</c>/commit as the aggregate it announces — never before, never separately.
    /// </summary>
    void Enqueue(OutboxMessage message);
}
