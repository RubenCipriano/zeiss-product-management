using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductManagement.Infrastructure.Edge;
using ProductManagement.Infrastructure.Redis;
using StackExchange.Redis;

namespace ProductManagement.Infrastructure.Idempotency;

/// <summary>
/// Idempotency keys in Redis. The claim is <c>SET key value NX</c>: atomic, so when the same request reaches two replicas
/// at once exactly one of them gets to process it.
/// </summary>
internal sealed partial class RedisIdempotencyStore(
    IConnectionMultiplexer redis,
    IOptions<IdempotencyOptions> options,
    IOptions<RedisOptions> redisOptions,
    ILogger<RedisIdempotencyStore> logger) : IIdempotencyStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IdempotencyOptions _options = options.Value;

    public async Task<IdempotencyClaim> TryBeginAsync(string key, string fingerprint, CancellationToken cancellationToken)
    {
        try
        {
            var database = redis.GetDatabase();
            var inProgress = JsonSerializer.Serialize(new Entry(fingerprint, null), Json);
            if (await database.StringSetAsync(RedisKey(key), inProgress, _options.InProgressTimeout, When.NotExists))
            {
                return new IdempotencyClaim(IdempotencyClaimStatus.Started);
            }

            var existing = await database.StringGetAsync(RedisKey(key));
            if (!existing.HasValue)
            {
                // Expired between the two calls: treat as in progress; the client's retry will claim it.
                return new IdempotencyClaim(IdempotencyClaimStatus.InProgress);
            }

            var entry = JsonSerializer.Deserialize<Entry>((string)existing!, Json)!;
            if (entry.Fingerprint != fingerprint)
            {
                return new IdempotencyClaim(IdempotencyClaimStatus.FingerprintMismatch);
            }

            return entry.Response is null
                ? new IdempotencyClaim(IdempotencyClaimStatus.InProgress)
                : new IdempotencyClaim(IdempotencyClaimStatus.Completed, entry.Response);
        }
        catch (Exception exception) when (RedisFailures.IsTransient(exception))
        {
            LogUnavailable(exception);
            return new IdempotencyClaim(IdempotencyClaimStatus.Unavailable);
        }
    }

    public async Task CompleteAsync(
        string key,
        string fingerprint,
        StoredResponse response,
        CancellationToken cancellationToken)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(
                RedisKey(key),
                JsonSerializer.Serialize(new Entry(fingerprint, response), Json),
                _options.KeyLifetime);
        }
        catch (Exception exception) when (RedisFailures.IsTransient(exception))
        {
            // The request was processed; a retry will see "in progress" until the claim times out, never a duplicate.
            LogUnavailable(exception);
        }
    }

    public async Task AbandonAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(RedisKey(key));
        }
        catch (Exception exception) when (RedisFailures.IsTransient(exception))
        {
            LogUnavailable(exception);
        }
    }

    private RedisKey RedisKey(string key) => $"{redisOptions.Value.KeyPrefix}:idempotency:{key}";

    private sealed record Entry(string Fingerprint, StoredResponse? Response);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Idempotency store (Redis) unavailable")]
    private partial void LogUnavailable(Exception exception);
}
