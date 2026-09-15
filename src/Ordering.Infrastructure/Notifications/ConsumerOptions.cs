namespace Ordering.Infrastructure.Notifications;

/// <summary>Bound from <c>Notifications:Consumer</c>.</summary>
public sealed class ConsumerOptions
{
    public const string SectionName = "Notifications:Consumer";

    /// <summary>Off in test hosts that do not run a broker.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Unacked deliveries in flight per consumer instance; also its handler concurrency.</summary>
    public ushort Prefetch { get; set; } = 50;

    /// <summary>Delivery attempts before the row is <c>Failed</c>; retry tiers exist for 1 … MaxAttempts − 1.</summary>
    public int MaxAttempts { get; set; } = 5;

    public int BaseBackoffMs { get; set; } = 200;

    public int MaxBackoffMs { get; set; } = 30_000;

    public TimeSpan BaseBackoff => TimeSpan.FromMilliseconds(BaseBackoffMs);

    public TimeSpan MaxBackoff => TimeSpan.FromMilliseconds(MaxBackoffMs);
}
