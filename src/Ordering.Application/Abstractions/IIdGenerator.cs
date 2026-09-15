namespace Ordering.Application.Abstractions;

/// <summary>
/// Issues 64-bit, time-ordered ids (Snowflake layout). Ids are generated before insert so an
/// outbox event id is known — and stable — from the moment the row is written.
/// </summary>
public interface IIdGenerator
{
    long NewId();
}
