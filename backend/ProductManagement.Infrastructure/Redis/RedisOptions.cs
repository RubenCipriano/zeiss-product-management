using System.ComponentModel.DataAnnotations;

namespace ProductManagement.Infrastructure.Redis;

/// <summary>
/// Settings shared by everything kept in Redis: the catalogue cache, idempotency keys, rate-limit buckets and sign-in
/// failures. Redis is used when the <c>Redis</c> connection string is configured.
/// </summary>
/// <remarks>
/// Bound to the <c>Cache</c> section, where <see cref="KeyPrefix"/> has always been configured (the cache was the first
/// user of Redis), so existing configuration keeps working.
/// </remarks>
internal sealed class RedisOptions
{
    public const string SectionName = "Cache";

    public const string ConnectionStringName = "Redis";

    /// <summary>Namespace for every key, so several applications/environments can share one Redis.</summary>
    [Required(AllowEmptyStrings = false)]
    public string KeyPrefix { get; init; } = "product-management";

    /// <summary>Upper bound for connecting; the connection string may only lower it.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:01:00")]
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Upper bound for a single command; the connection string may only lower it. Short on purpose: a slow or missing
    /// Redis must cost milliseconds, not seconds, because every user of it then falls back to something else.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:01:00")]
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
}
