namespace Ordering.Api.Sse;

/// <summary>Bound from the <c>Sse</c> section.</summary>
public sealed class SseOptions
{
    public const string SectionName = "Sse";

    /// <summary>
    /// A stream is closed after this long; <c>EventSource</c> reconnects and the first frame
    /// re-sends the full state, so nothing is lost and no connection lives forever.
    /// </summary>
    public int MaxConnectionSeconds { get; set; } = 300;

    /// <summary>Open streams per instance; beyond it a connect is <c>503 sse.full</c>. Capacity, not correctness.</summary>
    public int MaxConnections { get; set; } = 1000;

    public TimeSpan MaxConnection => TimeSpan.FromSeconds(MaxConnectionSeconds);
}
