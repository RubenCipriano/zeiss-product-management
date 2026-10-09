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

    private readonly TimeSpan _productGenerationTtl = options.Value.ProductGenerationTtl;

    private RedisKey DataGeneration => $"{_prefix}:catalog:generation";

    private RedisKey ListingGeneration => $"{_prefix}:catalog:listing-generation";

    private RedisKey ProductGeneration(int productId) => $"{_prefix}:catalog:product:{productId}:generation";

    public async Task<string?> GetVersionAsync(CacheScope scope, int productId = 0)
    {
        RedisKey[] keys = scope switch
        {
            CacheScope.Listing => [ListingGeneration],
            CacheScope.Product => [ListingGeneration, ProductGeneration(productId)],
            _ => [DataGeneration],
        };

        try
        {
            var values = await Database.StringGetAsync(keys);
            if (values.Any(value => !value.HasValue))
            {
                // First use, or the counter expired or was lost. Start from the clock instead of 0, so a version is
                // never reused for a different state. Only the first writer wins (NX).
                await Task.WhenAll(keys.Select(StartAsync));
                values = await Database.StringGetAsync(keys);
            }

            return string.Join('.', values.Select(value => (long)value));
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

    public async Task InvalidateAsync(CatalogChange change, IReadOnlyCollection<int> productIds)
    {
        var keys = new List<RedisKey> { DataGeneration };
        if (change == CatalogChange.Products)
        {
            keys.Add(ListingGeneration);
        }

        keys.AddRange(productIds.Select(ProductGeneration));
        try
        {
            // Pipelined: one round trip's latency for all counters.
            await Task.WhenAll(keys.Select(IncrementAsync));
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            // The write itself succeeded; readers may see the previous state until the entries' TTL expires.
            LogInvalidationFailed(exception);
        }
    }

    // A missing counter starts from the clock here too (INCR alone would start it at 1).
    private async Task IncrementAsync(RedisKey key)
    {
        await StartAsync(key);
        await Database.StringIncrementAsync(key);
    }

    // Per-product counters expire (there can be one per product); the catalogue-wide ones do not.
    private Task StartAsync(RedisKey key) =>
        Database.StringSetAsync(
            key,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            key == DataGeneration || key == ListingGeneration ? null : _productGenerationTtl,
            When.NotExists);

    private RedisKey Key(string key) => $"{_prefix}:{key}";

    private static bool IsCacheFailure(Exception exception) =>
        exception is RedisException or TimeoutException or ObjectDisposedException;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache unavailable while trying to {Operation}; using the database")]
    private partial void LogCacheUnavailable(Exception exception, string operation);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cache invalidation failed; cached reads may be stale until they expire")]
    private partial void LogInvalidationFailed(Exception exception);
}
