using Microsoft.AspNetCore.Http.Features;

namespace Ordering.Api.Sse;

/// <summary>The wire format of Server-Sent Events, in one place: headers, event frames, comments.</summary>
internal static class SseFrames
{
    public const string ContentType = "text/event-stream";

    /// <summary>Everything a proxy or browser needs to treat the response as a live stream.</summary>
    public static void StartStream(HttpContext context)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentType;
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    /// <summary><c>event:</c>, <c>data:</c>, blank line, flush. <paramref name="data"/> must not contain newlines (compact JSON never does).</summary>
    public static async Task WriteEventAsync(HttpResponse response, string @event, string data, CancellationToken cancellationToken)
    {
        await response.WriteAsync($"event: {@event}\ndata: {data}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>A comment line the client ignores; keeps proxies and the socket alive.</summary>
    public static async Task WriteCommentAsync(HttpResponse response, string comment, CancellationToken cancellationToken)
    {
        await response.WriteAsync($": {comment}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
