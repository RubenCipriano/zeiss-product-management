using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductManagement.Infrastructure.Edge;
using ProductManagement.Infrastructure.Redis;
using StackExchange.Redis;

namespace ProductManagement.Infrastructure.Security;

/// <summary>
/// Token-bucket rate limiting stored in Redis (see <see cref="IDistributedRateLimiter"/>). A small Lua script, which
/// Redis runs as one indivisible step, refills the bucket for the time that passed, takes a token if there is one and
/// saves the bucket. The time comes from Redis itself, so every replica sees the same bucket and the same clock.
/// </summary>
internal partial class RedisDistributedRateLimiter(
    IConnectionMultiplexer redis,
    IOptions<RedisOptions> options,
    ILogger<RedisDistributedRateLimiter> logger) : IDistributedRateLimiter
{
    // KEYS[1] = bucket; ARGV = tokens per second, capacity. Returns { allowed (0/1), retry after in ms }.
    private static readonly LuaScript TakeScript = LuaScript.Prepare(
        """
        local rate = tonumber(@rate)
        local capacity = tonumber(@capacity)
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        local state = redis.call('HMGET', @bucket, 'tokens', 'at')
        local tokens = tonumber(state[1]) or capacity
        local at = tonumber(state[2]) or now
        tokens = math.min(capacity, tokens + math.max(0, now - at) * rate / 1000)
        local allowed = 0
        local retry = 0
        if tokens >= 1 then
          tokens = tokens - 1
          allowed = 1
        else
          retry = math.ceil((1 - tokens) * 1000 / rate)
        end
        redis.call('HSET', @bucket, 'tokens', tostring(tokens), 'at', tostring(now))
        redis.call('PEXPIRE', @bucket, math.ceil(capacity * 1000 / rate) + 1000)
        return { allowed, retry }
        """);

    private readonly string _prefix = options.Value.KeyPrefix;

    public async Task<RateLimitDecision?> TryTakeAsync(
        string bucket,
        int tokensPerSecond,
        int capacity,
        CancellationToken cancellationToken)
    {
        try
        {
            // The limits are part of the key: instances configured differently never share (and confuse) a bucket.
            RedisKey key = $"{_prefix}:ratelimit:{tokensPerSecond}:{capacity}:{bucket}";
            var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
                TakeScript,
                new { bucket = key, rate = tokensPerSecond, capacity }))!;
            return new RateLimitDecision((int)result[0] == 1, TimeSpan.FromMilliseconds((int)result[1]));
        }
        catch (Exception exception) when (RedisFailures.IsTransient(exception))
        {
            LogUnavailable(exception);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shared rate limits unavailable; using per-replica limits")]
    private partial void LogUnavailable(Exception exception);
}

/// <summary>Without Redis there is nothing to share: per-replica limits apply.</summary>
internal class LocalOnlyRateLimiter : IDistributedRateLimiter
{
    public Task<RateLimitDecision?> TryTakeAsync(
        string bucket,
        int tokensPerSecond,
        int capacity,
        CancellationToken cancellationToken) => Task.FromResult<RateLimitDecision?>(null);
}
