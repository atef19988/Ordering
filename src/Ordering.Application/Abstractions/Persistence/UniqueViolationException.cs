namespace Ordering.Application.Abstractions.Persistence;

/// <summary>
/// The database refused an insert because a unique constraint or index already holds the key.
/// Thrown by Infrastructure in place of the provider's exception so a handler can react to a
/// specific constraint (<c>pk_idempotency_keys</c>) without knowing SQL Server error numbers.
/// </summary>
public sealed class UniqueViolationException(string constraintName, Exception innerException)
    : Exception($"Unique constraint '{constraintName}' was violated.", innerException)
{
    public string ConstraintName { get; } = constraintName;
}
