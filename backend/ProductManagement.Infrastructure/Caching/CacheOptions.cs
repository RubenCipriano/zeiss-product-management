namespace ProductManagement.Infrastructure.Caching;

/// <summary>
/// Read-model cache settings. Caching is enabled when the <c>Redis</c> connection string is configured.
/// </summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    public const string ConnectionStringName = "Redis";

    /// <summary>Namespace for every key, so several applications/environments can share one Redis.</summary>
    public string KeyPrefix { get; init; } = "product-management";

    /// <summary>Time to live of a single cached product.</summary>
    public TimeSpan ProductTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Time to live of a cached page of a stock-level query or a stock history.</summary>
    public TimeSpan ListTtl { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Time to live of the ids and counts of the full list and of searches. They are invalidated explicitly (listing
    /// generation), so this only bounds memory; longer means fewer expensive recomputations.
    /// </summary>
    public TimeSpan ListingTtl { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Time to live of a product's generation counter (one per changed product). Longer than any entry that uses it;
    /// if it expires or is evicted, the counter restarts from the clock, so a version is never reused.
    /// </summary>
    public TimeSpan ProductGenerationTtl { get; init; } = TimeSpan.FromHours(1);
}
