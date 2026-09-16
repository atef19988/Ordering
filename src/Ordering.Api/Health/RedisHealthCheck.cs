using Microsoft.Extensions.Diagnostics.HealthChecks;
using Ordering.Infrastructure.Redis;

namespace Ordering.Api.Health;

/// <summary>
/// Degraded, never unhealthy: without Redis the API serves the catalogue from the database and
/// the gate stays open, so it keeps taking traffic. <c>/health</c> stays 200 with
/// <c>Degraded</c> so an operator sees it and a load balancer does not pull the instance.
/// </summary>
public sealed class RedisHealthCheck(RedisConnection connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await connection.IsConnectedAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded("Redis is unreachable; catalogue reads are uncached.");
}
