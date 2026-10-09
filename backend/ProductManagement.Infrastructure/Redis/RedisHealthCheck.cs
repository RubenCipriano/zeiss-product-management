using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace ProductManagement.Infrastructure.Redis;

/// <summary>
/// Reports Redis as <see cref="HealthStatus.Degraded"/>, never Unhealthy, when it is unreachable: the API keeps
/// serving from the database, so a cache outage must not take replicas out of the load balancer.
/// </summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"Redis reachable ({latency.TotalMilliseconds:F1} ms).");
        }
        catch (Exception exception) when (RedisFailures.IsTransient(exception))
        {
            return HealthCheckResult.Degraded("Redis unreachable; serving from the database.", exception);
        }
    }
}
