namespace ProductManagement.Infrastructure.Caching;

/// <summary>
/// Cache for product read models, invalidated by <em>generation</em>: every key embeds a generation number, and a
/// write increments it. Entries of older generations are never read again and expire on their own. See
/// docs/adr/0008-redis-cache.md.
/// </summary>
/// <remarks>
/// <para>
/// Two generations, because writes differ in what they change. <see cref="CacheGeneration.Data"/> moves on with
/// every write. <see cref="CacheGeneration.Listing"/> only moves when products are created, edited or deleted: it
/// versions which products a list or search contains and in what order, which a stock change cannot affect. Under a
/// constant stream of stock changes, the expensive part of a list (finding the page and counting the matches) stays
/// cached.
/// </para>
/// <para>
/// Implementations must <b>fail open</b>: if the cache is unreachable, reads fall back to the database and nothing
/// throws. The cache is an optimisation, never a dependency for correctness.
/// </para>
/// </remarks>
internal interface ICatalogCache
{
    /// <summary>The current generation, or <c>null</c> when the cache is unavailable (callers then bypass it).</summary>
    Task<long?> GetGenerationAsync(CacheGeneration generation);

    Task<T?> GetAsync<T>(string key)
        where T : class;

    Task SetAsync<T>(string key, T value, TimeSpan timeToLive)
        where T : class;

    /// <summary>Makes the entries affected by a committed write obsolete. Call after the commit.</summary>
    Task InvalidateAsync(CatalogChange change);
}

internal enum CacheGeneration
{
    /// <summary>Anything a read returns, stock included. Moves on with every write.</summary>
    Data,

    /// <summary>Which products exist, with their names: membership and order of lists and searches.</summary>
    Listing,
}

internal enum CatalogChange
{
    /// <summary>Only stock quantities changed.</summary>
    Stock,

    /// <summary>Products were created, edited or deleted.</summary>
    Products,
}
