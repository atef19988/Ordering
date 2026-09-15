namespace Ordering.Infrastructure.Outbox;

/// <summary>Bound from <c>Notifications:Relay</c>.</summary>
public sealed class RelayOptions
{
    public const string SectionName = "Notifications:Relay";

    /// <summary>Off in test hosts that do not run a broker.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Sleep after an empty or partial claim; a full batch loops immediately.</summary>
    public int PollIntervalMs { get; set; } = 200;

    public int BatchSize { get; set; } = 200;

    /// <summary>How long a claim is honoured before the reaper may hand the row to another relay.</summary>
    public int LeaseSeconds { get; set; } = 30;

    public int ReaperIntervalSeconds { get; set; } = 60;

    /// <summary>A published row with no verdict after this long is re-published (at-least-once).</summary>
    public int PublishedTimeoutSeconds { get; set; } = 300;

    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollIntervalMs);

    public TimeSpan Lease => TimeSpan.FromSeconds(LeaseSeconds);

    public TimeSpan ReaperInterval => TimeSpan.FromSeconds(ReaperIntervalSeconds);

    public TimeSpan PublishedTimeout => TimeSpan.FromSeconds(PublishedTimeoutSeconds);
}
