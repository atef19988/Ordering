namespace Ordering.Application.Idempotency;

/// <summary>
/// Write side: stage the key row in the unit of work (EF). Read side: look a committed key up on
/// a connection of its own, because the handler asks <em>after</em> rolling back — the
/// transaction's connection is gone, and a second connection would otherwise have blocked on our
/// own uncommitted rows.
/// </summary>
public interface IIdempotencyStore
{
    void Add(IdempotencyKey key);

    Task<IdempotencyKey?> FindAsync(string key, CancellationToken cancellationToken);
}
