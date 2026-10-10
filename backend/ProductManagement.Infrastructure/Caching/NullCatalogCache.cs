namespace ProductManagement.Infrastructure.Caching;

/// <summary>
/// The "no cache" stand-in, used when Redis is not configured: it never finds anything and stores nothing, so every
/// read goes to the database and no other class needs an "is caching on?" check.
/// </summary>
internal class NullCatalogCache : ICatalogCache
{
    public Task<string?> GetVersionAsync(CacheScope scope, int productId = 0) => Task.FromResult<string?>(null);

    public Task<T?> GetAsync<T>(string key)
        where T : class => Task.FromResult<T?>(null);

    public Task SetAsync<T>(string key, T value, TimeSpan timeToLive)
        where T : class => Task.CompletedTask;

    public Task InvalidateAsync(CatalogChange change, IReadOnlyCollection<int> productIds) => Task.CompletedTask;
}
