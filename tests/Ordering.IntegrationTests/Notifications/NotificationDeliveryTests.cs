using System.Text.Json;
using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Outbox;
using Ordering.Infrastructure.Messaging;
using Ordering.Infrastructure.Notifications;
using Ordering.Infrastructure.Outbox;
using Ordering.IntegrationTests.Api;
using Ordering.IntegrationTests.Backend;
using Ordering.IntegrationTests.Persistence;
using static Ordering.IntegrationTests.Api.OrdersApi;

namespace Ordering.IntegrationTests.Notifications;

/// <summary>
/// Task 8 required test 6, plus the at-least-once cases <c>docs/architecture.md</c> §6 promises,
/// all the long way round: outbox row → relay → RabbitMQ → consumer → delivery. Every host is a
/// real process with the relay and consumer on; a "crash" is a host stopped while it holds an
/// unacknowledged delivery, and the broker really redelivers to the next host.
/// </summary>
[Collection(BackendCollection.Name)]
public sealed class NotificationDeliveryTests(BackendFixture backend) : IAsyncLifetime
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private readonly SqlServerFixture _db = backend.Sql;

    public Task InitializeAsync() => backend.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Required test 6a.</summary>
    [Fact]
    public async Task Notification_WhenDeliveryFailsTwice_IsSentOnTheThirdAttempt_ThroughBroker()
    {
        await using var host = Host(workerId: 11, ("Notifications:Delivery:FailureMode", nameof(DeliveryFailureMode.FailFirstN)), ("Notifications:Delivery:FailFirstN", "2"));
        using var client = host.CreateClient();
        var orderId = await client.CreateOrderAsync(OrderBody(Customer(), ("SKU-001", 1)));

        var order = await Concurrency.WaitForAsync(() => client.GetOrderAsync(orderId), o => NotificationStatusOf(o) == "Sent", what: "notificationStatus = Sent");

        Assert.Equal(3, order.GetProperty("notificationAttempts").GetInt32());
        var row = await _db.OutboxRowAsync(orderId);
        Assert.Equal((nameof(OutboxMessageStatus.Sent), 3), (row.Status, row.AttemptCount));
        Assert.Null(row.LastError);
        Assert.Equal(
            [(row.Id, 1, false), (row.Id, 2, false), (row.Id, 3, true)],
            host.Deliveries.Calls.Select(c => (c.EventId, c.Attempt, c.Succeeded)));
    }

    /// <summary>Required test 6b: after the last attempt the row is terminal, nothing is queued anywhere, and nothing moves.</summary>
    [Fact]
    public async Task Notification_WhenDeliveryAlwaysFails_IsFailedAfterMaxAttemptsAndNeverRetriedAgain_ThroughBroker()
    {
        await using var host = Host(workerId: 12, ("Notifications:Delivery:FailureMode", nameof(DeliveryFailureMode.AlwaysFail)), ("Notifications:Consumer:MaxAttempts", "3"));
        using var client = host.CreateClient();
        var orderId = await client.CreateOrderAsync(OrderBody(Customer(), ("SKU-001", 1)));

        var order = await Concurrency.WaitForAsync(() => client.GetOrderAsync(orderId), o => NotificationStatusOf(o) == "Failed", what: "notificationStatus = Failed");

        Assert.Equal(3, order.GetProperty("notificationAttempts").GetInt32());
        var row = await _db.OutboxRowAsync(orderId);
        Assert.Equal((nameof(OutboxMessageStatus.Failed), 3), (row.Status, row.AttemptCount));
        Assert.Contains("AlwaysFail", row.LastError, StringComparison.Ordinal);

        await Concurrency.SettleAsync(Settle);

        Assert.Equal((nameof(OutboxMessageStatus.Failed), 3), await StatusAndAttemptsAsync(orderId));
        Assert.Equal(3, host.Deliveries.Calls.Count);
        Assert.Equal(0, host.Deliveries.SendsOf(row.Id));
        Assert.Equal("Failed", NotificationStatusOf(await client.GetOrderAsync(orderId)));
        foreach (var queue in RabbitMqFixture.Queues)
        {
            Assert.Equal(0u, await backend.RabbitMq.ReadyCountAsync(queue));
        }
    }

    /// <summary>
    /// The process dies after writing <c>Sent</c> but before acknowledging the broker. The broker
    /// redelivers to the next process, whose attempt statement finds the row terminal and
    /// acknowledges without sending: the customer gets one notification. Remove the
    /// <c>status = 'Processing'</c> guard from <see cref="OutboxStore.CountAttemptAsync"/> and this
    /// goes red (two sends, <c>attempt_count = 2</c>).
    /// </summary>
    [Fact]
    public async Task Notification_WhenTheProcessDiesAfterTheVerdictBeforeTheAck_IsNotSentAgainOnRedelivery_ThroughBroker()
    {
        await using var first = Host(workerId: 21);
        var parked = first.Failures.HoldAt(FailurePoints.NotificationSentBeforeAck);
        long orderId;

        using (var client = first.CreateClient())
        {
            orderId = await client.CreateOrderAsync(OrderBody(Customer(), ("SKU-001", 1)));
        }

        await parked.WaitAsync(Concurrency.DefaultTimeout);
        var row = await _db.OutboxRowAsync(orderId);
        Assert.Equal((nameof(OutboxMessageStatus.Sent), 1), (row.Status, row.AttemptCount));
        Assert.Equal(1, first.Deliveries.SendsOf(row.Id));

        await first.DisposeAsync(); // dies holding the unacknowledged delivery

        await using var second = Host(workerId: 22);
        using var probe = second.CreateClient();
        // Either the redelivery is acknowledged as a duplicate (the log line is its only trace) or it is sent again; wait for one, then assert which.
        await Concurrency.WaitForAsync(
            () => Task.FromResult(second.Logs.About(row.Id, "Duplicate delivery").Count + second.Deliveries.Calls.Count),
            handled => handled >= 1,
            what: "the second host to handle the redelivery");

        Assert.Empty(second.Deliveries.Calls);
        Assert.Single(second.Logs.About(row.Id, "Duplicate delivery"));
        Assert.Equal((nameof(OutboxMessageStatus.Sent), 1), await StatusAndAttemptsAsync(orderId));
        await Concurrency.SettleAsync(Settle);
        Assert.Empty(second.Deliveries.Calls);
        Assert.Equal((nameof(OutboxMessageStatus.Sent), 1), await StatusAndAttemptsAsync(orderId));
        Assert.Equal(0u, await backend.RabbitMq.ReadyCountAsync(RabbitMqTopology.OrderCreatedQueue));
        Assert.Equal(1, first.Deliveries.SendsOf(row.Id) + second.Deliveries.SendsOf(row.Id));
    }

    /// <summary>
    /// The process dies after the provider accepted the notification but before the verdict
    /// reached the row. Nothing in the database knows the send happened, so the redelivery is
    /// counted and sent again — this is the at-least-once duplicate the design documents, not a
    /// bug the test hides. What the design guarantees, and this asserts, is the shape of the
    /// duplicate: one outbox row, one event id on both sends, <c>attempt_count</c> tells the
    /// truth (2), and the second send carries the same id, which is the key a real provider
    /// deduplicates on. Exactly-once would need the provider to take part.
    /// </summary>
    [Fact]
    public async Task Notification_WhenTheProcessDiesAfterDeliveryBeforeTheVerdict_IsSentAgainWithTheSameEventId_ThroughBroker()
    {
        await using var first = Host(workerId: 31);
        var parked = first.Failures.HoldAt(FailurePoints.NotificationDeliveredBeforeVerdict);
        long orderId;

        using (var client = first.CreateClient())
        {
            orderId = await client.CreateOrderAsync(OrderBody(Customer(), ("SKU-001", 1)));
        }

        await parked.WaitAsync(Concurrency.DefaultTimeout);
        var row = await _db.OutboxRowAsync(orderId);
        Assert.Equal((nameof(OutboxMessageStatus.Processing), 1), (row.Status, row.AttemptCount));
        Assert.Equal(1, first.Deliveries.SendsOf(row.Id));

        await first.DisposeAsync(); // dies after the send, before Sent was written, before the ack

        await using var second = Host(workerId: 32);
        using var probe = second.CreateClient();
        var order = await Concurrency.WaitForAsync(() => probe.GetOrderAsync(orderId), o => NotificationStatusOf(o) == "Sent", what: "notificationStatus = Sent");

        Assert.Equal(2, order.GetProperty("notificationAttempts").GetInt32());
        Assert.Equal((nameof(OutboxMessageStatus.Sent), 2), await StatusAndAttemptsAsync(orderId));
        var duplicate = Assert.Single(second.Deliveries.Calls);
        Assert.Equal((row.Id, 2, true), (duplicate.EventId, duplicate.Attempt, duplicate.Succeeded));
        Assert.Equal(2, first.Deliveries.SendsOf(row.Id) + second.Deliveries.SendsOf(row.Id));
        Assert.Equal(1, await _db.CountAsync("SELECT COUNT(*) FROM outbox_messages WHERE aggregate_id = @orderId", new { orderId }));

        await Concurrency.SettleAsync(Settle);
        Assert.Equal((nameof(OutboxMessageStatus.Sent), 2), await StatusAndAttemptsAsync(orderId));
        Assert.Single(second.Deliveries.Calls);
    }

    /// <summary>Task 7's restart box: rows created while no relay or consumer runs are delivered once one starts.</summary>
    [Fact]
    public async Task Notification_CreatedWhileNothingRuns_IsDeliveredAfterAHostStarts_ThroughBroker()
    {
        long[] orderIds;

        await using (var quiet = new OrderingApiFactory(backend) { WorkerId = 41 })
        using (var client = quiet.CreateClient())
        {
            orderIds = [await client.CreateOrderAsync(OrderBody(Customer(), ("SKU-001", 1))), await client.CreateOrderAsync(OrderBody(Customer(), ("SKU-002", 1)))];
        }

        Assert.Equal(2, await _db.CountAsync("SELECT COUNT(*) FROM outbox_messages WHERE status = 'Pending'"));

        await using var host = Host(workerId: 42);
        _ = host.Services; // starts the host, relay and consumer included

        foreach (var orderId in orderIds)
        {
            await Concurrency.WaitForAsync(() => StatusAndAttemptsAsync(orderId), s => s == (nameof(OutboxMessageStatus.Sent), 1), what: $"order {orderId} Sent");
        }

        Assert.Equal(2, host.Deliveries.Calls.Count);
    }

    private OrderingApiFactory Host(int workerId, params (string Key, string Value)[] settings) => new(backend)
    {
        WorkerId = workerId,
        Messaging = true,
        Settings = settings.ToDictionary(s => s.Key, s => (string?)s.Value),
    };

    private async Task<(string Status, int Attempts)> StatusAndAttemptsAsync(long orderId)
    {
        var row = await _db.OutboxRowAsync(orderId);
        return (row.Status, row.AttemptCount);
    }

    private static string? NotificationStatusOf(JsonElement order) => order.GetProperty("notificationStatus").GetString();
}
