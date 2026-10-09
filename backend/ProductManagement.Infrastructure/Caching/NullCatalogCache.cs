namespace ProductManagement.Infrastructure.Caching;

/// <summary>Used when no Redis is configured: every read goes to the database.</summary>
internal sealed class NullCatalogCache : ICatalogCache
{
    public Task<string?> GetVersionAsync(CacheScope scope, int productId = 0) => Task.FromResult<string?>(null);

    public Task<T?> GetAsync<T>(string key)
        where T : class => Task.FromResult<T?>(null);

    public Task SetAsync<T>(string key, T value, TimeSpan timeToLive)
        where T : class => Task.CompletedTask;

    public Task InvalidateAsync(CatalogChange change, IReadOnlyCollection<int> productIds) => Task.CompletedTask;
}
