using ProductManagement.Infrastructure.Caching;

namespace ProductManagement.Infrastructure.UnitTests.Caching;

/// <summary>An in-memory <see cref="ICatalogCache"/> that records what is asked of it.</summary>
internal sealed class FakeCatalogCache : ICatalogCache
{
    public Dictionary<(CacheScope Scope, int ProductId), string> Versions { get; } = [];

    public Dictionary<string, object> Entries { get; } = new(StringComparer.Ordinal);

    public List<string> Reads { get; } = [];

    public List<(CatalogChange Change, int[] ProductIds)> Invalidations { get; } = [];

    public Task<string?> GetVersionAsync(CacheScope scope, int productId = 0) =>
        Task.FromResult(Versions.TryGetValue((scope, productId), out var version) ? version : null);

    public Task<T?> GetAsync<T>(string key)
        where T : class
    {
        Reads.Add(key);
        return Task.FromResult(Entries.TryGetValue(key, out var value) ? (T)value : null);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan timeToLive)
        where T : class
    {
        Entries[key] = value;
        return Task.CompletedTask;
    }

    public Task InvalidateAsync(CatalogChange change, IReadOnlyCollection<int> productIds)
    {
        Invalidations.Add((change, productIds.ToArray()));
        return Task.CompletedTask;
    }
}
