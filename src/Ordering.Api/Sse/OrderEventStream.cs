using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Ordering.Api.Endpoints;
using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Messaging;
using Ordering.Application.Features.Orders.GetOrderById;
using Ordering.Domain.Orders;

namespace Ordering.Api.Sse;

/// <summary>
/// <c>GET /api/orders/{id}/events</c>. The stream carries state, never deltas: the full
/// <see cref="OrderDetailDto"/> on connect and again after every change hint, each one a fresh
/// read through the Task 3 query, so a lost or duplicated hint cannot desynchronise a client.
/// A <c>: ping</c> every <see cref="PingInterval"/> keeps proxies alive; the stream closes after
/// <c>Sse:MaxConnectionSeconds</c> (the browser reconnects and gets the state again) and ends
/// with <c>event: done</c> once nothing can change any more: the order is cancelled and its
/// notification has a verdict.
/// </summary>
public sealed class OrderEventStream(
    IDispatcher dispatcher,
    IOrderChangeHub hub,
    SseConnections connections,
    SseOptions options,
    IOptions<JsonOptions> json)
{
    public const string OrderEvent = "order";
    public const string DoneEvent = "done";

    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

    public async Task RunAsync(long orderId, HttpContext context, CancellationToken cancellationToken)
    {
        if (!connections.TryAcquire(options.MaxConnections))
        {
            await Result.Failure(SseErrors.Full(options.MaxConnections)).ToHttpResult().ExecuteAsync(context);
            return;
        }

        try
        {
            // Subscribe before the first read: a hint that lands between the two is not missed.
            using var subscription = hub.Subscribe(orderId);
            var first = await dispatcher.Query(new GetOrderByIdQuery(orderId), cancellationToken);

            if (first.IsFailure)
            {
                await first.ToHttpResult().ExecuteAsync(context);
                return;
            }

            SseFrames.StartStream(context);
            await StreamAsync(orderId, first.Value, subscription.Hints, context.Response, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The client went away or the connection reached its maximum age; either way, close quietly.
        }
        finally
        {
            connections.Release();
        }
    }

    private async Task StreamAsync(long orderId, OrderDetailDto first, ChannelReader<long> hints, HttpResponse response, CancellationToken cancellationToken)
    {
        if (await SendAsync(response, first, cancellationToken))
        {
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(options.MaxConnection);

        while (!lifetime.IsCancellationRequested)
        {
            if (!await WaitForHintAsync(hints, lifetime.Token))
            {
                await SseFrames.WriteCommentAsync(response, "ping", lifetime.Token);
                continue;
            }

            // Every queued hint means the same thing — re-read — so one read serves them all.
            while (hints.TryRead(out _))
            {
            }

            var current = await dispatcher.Query(new GetOrderByIdQuery(orderId), lifetime.Token);

            if (current.IsFailure)
            {
                // The row is gone (hand-deleted); the reconnect will get the 404.
                return;
            }

            if (await SendAsync(response, current.Value, lifetime.Token))
            {
                return;
            }
        }
    }

    /// <summary>Sends the order; when it is terminal, also sends <c>done</c> and returns true.</summary>
    private async Task<bool> SendAsync(HttpResponse response, OrderDetailDto order, CancellationToken cancellationToken)
    {
        await SseFrames.WriteEventAsync(response, OrderEvent, JsonSerializer.Serialize(order, json.Value.SerializerOptions), cancellationToken);

        if (!IsTerminal(order))
        {
            return false;
        }

        await SseFrames.WriteEventAsync(response, DoneEvent, string.Empty, cancellationToken);
        return true;
    }

    /// <summary>True on a hint; false when <see cref="PingInterval"/> passed without one. Throws when <paramref name="cancellationToken"/> fires.</summary>
    private static async Task<bool> WaitForHintAsync(ChannelReader<long> hints, CancellationToken cancellationToken)
    {
        using var ping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ping.CancelAfter(PingInterval);

        try
        {
            return await hints.WaitToReadAsync(ping.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Cancelled, and the notification is <c>Sent</c> or <c>Failed</c>: no path in the system writes this order again.</summary>
    private static bool IsTerminal(OrderDetailDto order) =>
        order.Status == nameof(OrderStatus.Cancelled)
        && order.NotificationStatus is NotificationStatus.Sent or NotificationStatus.Failed;
}
