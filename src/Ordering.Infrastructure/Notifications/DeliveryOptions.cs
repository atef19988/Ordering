namespace Ordering.Infrastructure.Notifications;

public enum DeliveryFailureMode
{
    None,

    AlwaysFail,

    /// <summary>Fails while <c>attempt &lt;= FailFirstN</c>; deterministic per message because the attempt number comes from the outbox row.</summary>
    FailFirstN,

    /// <summary>Fails with probability <see cref="DeliveryOptions.FailureRate"/>.</summary>
    Random,
}

/// <summary>Bound from <c>Notifications:Delivery</c>; drives <see cref="FakeDeliveryService"/>.</summary>
public sealed class DeliveryOptions
{
    public const string SectionName = "Notifications:Delivery";

    public DeliveryFailureMode FailureMode { get; set; } = DeliveryFailureMode.None;

    public int FailFirstN { get; set; } = 2;

    public double FailureRate { get; set; } = 0.3;

    /// <summary>Simulated provider latency, applied to every call.</summary>
    public int LatencyMs { get; set; } = 50;
}
