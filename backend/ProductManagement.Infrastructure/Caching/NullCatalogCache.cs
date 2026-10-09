namespace ProductManagement.Infrastructure.Caching;

/// <summary>Used when no Redis is configured: every read goes to the database.</summary>
internal sealed class NullCatalogCache : ICatalogCache
{
    public Task<long?> GetGenerationAsync(CacheGeneration generation) => Task.FromResult<long?>(null);

    public Task<T?> GetAsync<T>(string key)
        where T : class => Task.FromResult<T?>(null);

    public Task SetAsync<T>(string key, T value, TimeSpan timeToLive)
        where T : class => Task.CompletedTask;

    public Task InvalidateAsync(CatalogChange change) => Task.CompletedTask;
}
