using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ProductManagement.Infrastructure.Caching;

/// <summary>
/// Redis implementation of <see cref="ICatalogCache"/>. Shared by every API replica, so a write on one replica is
/// visible to reads on all the others as soon as the generation is incremented.
/// </summary>
internal sealed partial class RedisCatalogCache(
    IConnectionMultiplexer redis,
    IOptions<CacheOptions> options,
    ILogger<RedisCatalogCache> logger) : ICatalogCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _prefix = options.Value.KeyPrefix;

    private IDatabase Database => redis.GetDatabase();

    private RedisKey GenerationKey(CacheGeneration generation) => generation switch
    {
        CacheGeneration.Listing => $"{_prefix}:catalog:listing-generation",
        _ => $"{_prefix}:catalog:generation",
    };

    public async Task<long?> GetGenerationAsync(CacheGeneration generation)
    {
        var key = GenerationKey(generation);
        try
        {
            var value = await Database.StringGetAsync(key);
            if (value.HasValue)
            {
                return (long)value;
            }

            // First use, or Redis lost its data. Start from the clock instead of 0, so a generation number is never
            // reused for a different state of the catalogue. Only the first writer wins (NX).
            await Database.StringSetAsync(key, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), when: When.NotExists);
            return (long)await Database.StringGetAsync(key);
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            LogCacheUnavailable(exception, "read the generation");
            return null;
        }
    }

    public async Task<T?> GetAsync<T>(string key)
        where T : class
    {
        try
        {
            var value = await Database.StringGetAsync(Key(key));
            return value.HasValue ? JsonSerializer.Deserialize<T>((string)value!, SerializerOptions) : null;
        }
        catch (Exception exception) when (IsCacheFailure(exception) || exception is JsonException)
        {
            LogCacheUnavailable(exception, "read an entry");
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan timeToLive)
        where T : class
    {
        try
        {
            await Database.StringSetAsync(Key(key), JsonSerializer.Serialize(value, SerializerOptions), timeToLive);
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            LogCacheUnavailable(exception, "write an entry");
        }
    }

    public async Task InvalidateAsync(CatalogChange change)
    {
        try
        {
            await IncrementAsync(GenerationKey(CacheGeneration.Data));
            if (change == CatalogChange.Products)
            {
                await IncrementAsync(GenerationKey(CacheGeneration.Listing));
            }
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            // The write itself succeeded; readers may see the previous state until the entries' TTL expires.
            LogInvalidationFailed(exception);
        }
    }

    // A missing generation starts from the clock here too (INCR alone would start it at 1).
    private async Task IncrementAsync(RedisKey key)
    {
        await Database.StringSetAsync(key, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), when: When.NotExists);
        await Database.StringIncrementAsync(key);
    }

    private RedisKey Key(string key) => $"{_prefix}:{key}";

    private static bool IsCacheFailure(Exception exception) =>
        exception is RedisException or TimeoutException or ObjectDisposedException;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache unavailable while trying to {Operation}; using the database")]
    private partial void LogCacheUnavailable(Exception exception, string operation);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cache invalidation failed; cached reads may be stale until they expire")]
    private partial void LogInvalidationFailed(Exception exception);
}
