namespace Ordering.Application.Abstractions;

/// <summary>
/// A test seam, nothing more. Production registers <see cref="NoFailurePoint"/>, which returns
/// at once; the integration-test host swaps in an implementation that can throw or hold the
/// caller at a named point, so a test can prove what a crash <em>there</em> leaves behind —
/// a real rolled-back transaction, a real broker redelivery — instead of mocking the outcome.
/// The names are the contract; a point that nobody arms costs one dictionary miss.
/// </summary>
public interface IFailurePoint
{
    /// <summary>Called by the code under test when it reaches <paramref name="point"/>.</summary>
    ValueTask ReachedAsync(string point, CancellationToken cancellationToken);
}

/// <summary>The points the code under test reports, named after what has already happened when they are hit.</summary>
public static class FailurePoints
{
    /// <summary>The handler returned success; every statement ran; the transaction is not yet committed.</summary>
    public const string BeforeCommit = "transaction.before-commit";

    /// <summary>The notification was delivered; the outbox row does not yet say <c>Sent</c>.</summary>
    public const string NotificationDeliveredBeforeVerdict = "notification.delivered.before-verdict";

    /// <summary>The outbox row says <c>Sent</c>; the broker has not yet been acknowledged.</summary>
    public const string NotificationSentBeforeAck = "notification.sent.before-ack";
}

public sealed class NoFailurePoint : IFailurePoint
{
    public ValueTask ReachedAsync(string point, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
