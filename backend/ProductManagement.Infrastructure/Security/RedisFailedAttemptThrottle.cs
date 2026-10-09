using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductManagement.Application.Common.Security;
using ProductManagement.Infrastructure.Caching;
using StackExchange.Redis;

namespace ProductManagement.Infrastructure.Security;

/// <summary>
/// Fixed-window failure counter in Redis (<c>INCR</c> + <c>EXPIRE</c>), so the limit holds for the whole cluster no matter
/// which replica handles each attempt. If Redis is unavailable it fails open (logs a warning); the per-replica rate
/// limiter on the token endpoint still applies.
/// </summary>
internal sealed partial class RedisFailedAttemptThrottle(
    IConnectionMultiplexer redis,
    IOptions<FailedAttemptThrottleOptions> options,
    IOptions<CacheOptions> cacheOptions,
    ILogger<RedisFailedAttemptThrottle> logger) : IFailedAttemptThrottle
{
    private readonly FailedAttemptThrottleOptions _options = options.Value;

    public async Task<TimeSpan?> GetBlockedForAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var database = redis.GetDatabase();
            var failures = await database.StringGetAsync(Key(key));
            if (!failures.HasValue || (long)failures < _options.MaxFailures)
            {
                return null;
            }

            return await database.KeyTimeToLiveAsync(Key(key)) ?? _options.Window;
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            LogUnavailable(exception);
            return null;
        }
    }

    public async Task RecordFailureAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var database = redis.GetDatabase();
            var failures = await database.StringIncrementAsync(Key(key));
            if (failures == 1)
            {
                // The window starts with the first failure.
                await database.KeyExpireAsync(Key(key), _options.Window);
            }
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            LogUnavailable(exception);
        }
    }

    private RedisKey Key(string key) => $"{cacheOptions.Value.KeyPrefix}:signin-failures:{key}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis unavailable: sign-in failures are not being counted cluster-wide")]
    private partial void LogUnavailable(Exception exception);
}
