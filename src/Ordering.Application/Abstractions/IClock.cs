namespace Ordering.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
