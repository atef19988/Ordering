namespace Ordering.Infrastructure.Redis;

/// <summary>Bound from the <c>Redis</c> section. Defaults match <c>docker-compose.yml</c>.</summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>
    /// A StackExchange.Redis configuration string. <c>abortConnect=false</c> is forced on whatever
    /// is configured: the host must start, and stay up, without Redis.
    /// </summary>
    public string Configuration { get; set; } = "localhost:6379,abortConnect=false";

    /// <summary>Key prefix, so one Redis can serve several environments.</summary>
    public string InstanceName { get; set; } = "ordering:";
}
