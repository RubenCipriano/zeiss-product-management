using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ProductManagement.Infrastructure.Edge;

namespace ProductManagement.Infrastructure.Security;

/// <summary>
/// Single-process fallback used when no Redis is configured (local development only). Windows that have ended are
/// purged on access, so the table only holds keys that failed recently.
/// </summary>
internal sealed class InMemoryFailedAttemptThrottle(
    IOptions<FailedAttemptThrottleOptions> options,
    TimeProvider timeProvider) : IFailedAttemptThrottle
{
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset WindowEnds)> _attempts = new(StringComparer.Ordinal);
    private readonly FailedAttemptThrottleOptions _options = options.Value;

    public Task<TimeSpan?> GetBlockedForAsync(string key, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        PurgeExpired(now);
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

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var entry in _attempts)
        {
            if (entry.Value.WindowEnds <= now)
            {
                _attempts.TryRemove(entry);
            }
        }
    }
}
