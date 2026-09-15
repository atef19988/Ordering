using Microsoft.Extensions.Primitives;
using Ordering.Api.Endpoints;
using Serilog.Core;
using Serilog.Events;

namespace Ordering.Api.Logging;

/// <summary>
/// Stamps every log event written during a request with <c>RequestId</c> and, when the caller sent
/// one, <c>IdempotencyKey</c>, so a retried submission can be traced end to end.
/// </summary>
public sealed class HttpContextEnricher(IHttpContextAccessor httpContextAccessor) : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return;
        }

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("RequestId", httpContext.TraceIdentifier));

        if (httpContext.Request.Headers.TryGetValue(ApiHeaders.IdempotencyKey, out var key) && !StringValues.IsNullOrEmpty(key))
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("IdempotencyKey", key.ToString()));
        }
    }
}
