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

    /// <summary>
    /// Longest any single Redis command may take before it counts as a failure (sync and async
    /// timeouts). A cache that answers slower than the database is worse than no cache, and a
    /// black-holed connection must fail fast, so this is short on purpose.
    /// </summary>
    public int OperationTimeoutMs { get; set; } = 500;

    /// <summary>Longest a (re)connect attempt may take; paid once per attempt, in the background after the first.</summary>
    public int ConnectTimeoutMs { get; set; } = 2000;
}
