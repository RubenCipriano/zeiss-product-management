using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ProductManagement.Application.Common.Security;

namespace ProductManagement.Infrastructure.Security;

/// <summary>Single-process fallback used when no Redis is configured (local runs without the cluster).</summary>
internal sealed class InMemoryFailedAttemptThrottle(
    IOptions<FailedAttemptThrottleOptions> options,
    TimeProvider timeProvider) : IFailedAttemptThrottle
{
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset WindowEnds)> _attempts = new();
    private readonly FailedAttemptThrottleOptions _options = options.Value;

    public Task<TimeSpan?> GetBlockedForAsync(string key, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return Task.FromResult<TimeSpan?>(
            _attempts.TryGetValue(key, out var entry) && entry.WindowEnds > now && entry.Failures >= _options.MaxFailures
                ? entry.WindowEnds - now
                : null);
    }

    public Task RecordFailureAsync(string key, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        _attempts.AddOrUpdate(
            key,
            _ => (1, now + _options.Window),
            (_, entry) => entry.WindowEnds > now ? (entry.Failures + 1, entry.WindowEnds) : (1, now + _options.Window));
        return Task.CompletedTask;
    }
}
