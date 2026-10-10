using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductManagement.Infrastructure.Edge;
using ProductManagement.Infrastructure.Redis;
using StackExchange.Redis;

namespace ProductManagement.Infrastructure.Security;

/// <summary>
/// Counts failed sign-ins per caller in Redis: a counter (<c>INCR</c>) that expires at the end of its time window
/// (<c>EXPIRE</c>), so the limit holds for the whole cluster no matter which replica handles each attempt. If Redis is
/// unavailable it fails open (logs a warning and lets the attempt through); the per-replica rate limiter on the token
/// endpoint still applies.
/// </summary>
internal partial class RedisFailedAttemptThrottle(
    IConnectionMultiplexer redis,
    IOptions<FailedAttemptThrottleOptions> options,
    IOptions<RedisOptions> redisOptions,
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
        catch (Exception exception) when (RedisFailures.IsTransient(exception))
        {
            LogUnavailable(exception);
            return null;
        }
    }

    public async Task RecordFailureAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            // One transaction: count the failure, and start the window if the counter has none (EXPIRE … NX), so the
            // counter can never outlive its window. Two separate calls (INCR, then EXPIRE when the count is 1) left a
            // counter without expiry whenever the second call was lost, and that IP stayed blocked for good.
            var transaction = redis.GetDatabase().CreateTransaction();
            _ = transaction.StringIncrementAsync(Key(key));
            _ = transaction.KeyExpireAsync(Key(key), _options.Window, ExpireWhen.HasNoExpiry);
            await transaction.ExecuteAsync();
        }
        catch (Exception exception) when (RedisFailures.IsTransient(exception))
        {
            LogUnavailable(exception);
        }
    }

    private RedisKey Key(string key) => $"{redisOptions.Value.KeyPrefix}:signin-failures:{key}";

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Redis unavailable: sign-in failures are not being counted cluster-wide")]
    private partial void LogUnavailable(Exception exception);
}
