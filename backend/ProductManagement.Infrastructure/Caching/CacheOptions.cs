using System.ComponentModel.DataAnnotations;

namespace ProductManagement.Infrastructure.Caching;

/// <summary>
/// Read-model cache settings. Caching is enabled when Redis is configured (see <see cref="Redis.RedisOptions"/>, which
/// shares this section for the key prefix).
/// </summary>
internal sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>Time to live of a single cached product.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan ProductTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Time to live of a cached page of a stock-level query or a stock history.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan ListTtl { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Time to live of the ids and counts of the full list and of searches. They are invalidated explicitly (listing
    /// generation), so this only bounds memory; longer means fewer expensive recomputations.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan ListingTtl { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Time to live of a product's generation counter (one per changed product). Longer than any entry that uses it;
    /// if it expires or is evicted, the counter restarts from the clock, so a version is never reused.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "7.00:00:00")]
    public TimeSpan ProductGenerationTtl { get; init; } = TimeSpan.FromHours(1);
}
