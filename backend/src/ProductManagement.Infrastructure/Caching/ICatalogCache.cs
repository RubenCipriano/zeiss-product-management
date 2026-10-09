namespace ProductManagement.Infrastructure.Caching;

/// <summary>
/// Cache for product read models, invalidated by <em>generation</em>: every key embeds the current catalogue
/// generation, and any write increments it. Entries of older generations are never read again and expire on their
/// own. See docs/adr/0008-redis-cache.md.
/// </summary>
/// <remarks>
/// Implementations must <b>fail open</b>: if the cache is unreachable, reads fall back to the database and nothing
/// throws. The cache is an optimisation, never a dependency for correctness.
/// </remarks>
internal interface ICatalogCache
{
    /// <summary>The current generation, or <c>null</c> when the cache is unavailable (callers then bypass it).</summary>
    Task<long?> GetGenerationAsync();

    Task<T?> GetAsync<T>(string key)
        where T : class;

    Task SetAsync<T>(string key, T value, TimeSpan timeToLive)
        where T : class;

    /// <summary>Makes every cached entry obsolete. Call after a write has been committed.</summary>
    Task InvalidateAsync();
}
