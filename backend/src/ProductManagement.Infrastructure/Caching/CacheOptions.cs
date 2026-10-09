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

    /// <summary>Time to live of a cached page of a list/search/stock-level query.</summary>
    public TimeSpan ListTtl { get; init; } = TimeSpan.FromMinutes(1);
}
