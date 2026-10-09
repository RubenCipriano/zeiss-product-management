using System.Globalization;
using Microsoft.Extensions.Options;
using ProductManagement.Application.Common.Paging;
using ProductManagement.Application.Products.Abstractions;
using ProductManagement.Application.Products.Contracts;
using ProductManagement.Infrastructure.Products;

namespace ProductManagement.Infrastructure.Caching;

/// <summary>
/// Cache-aside decorator over <see cref="ProductQueries"/>. The application layer is unaware of caching: it still
/// depends on <see cref="IProductQueries"/>, and the decorator is wired in by dependency injection.
/// </summary>
internal sealed class CachingProductQueries(
    ProductQueries inner,
    ICatalogCache cache,
    IOptions<CacheOptions> options) : IProductQueries
{
    private readonly CacheOptions _options = options.Value;

    public Task<ProductResponse?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        GetOrLoadAsync(
            Invariant($"product:{id}"),
            _options.ProductTtl,
            () => inner.GetByIdAsync(id, cancellationToken));

    public Task<PagedResponse<ProductResponse>> ListAsync(PageQuery page, CancellationToken cancellationToken) =>
        GetOrLoadRequiredAsync(
            Invariant($"products:all:{PageKey(page)}"),
            () => inner.ListAsync(page, cancellationToken));

    public Task<PagedResponse<ProductResponse>> SearchByNameAsync(
        string nameFragment,
        PageQuery page,
        CancellationToken cancellationToken) =>
        GetOrLoadRequiredAsync(
            // The search is case-insensitive, so differently-cased terms share one entry.
            Invariant($"products:search:{nameFragment.ToUpperInvariant()}:{PageKey(page)}"),
            () => inner.SearchByNameAsync(nameFragment, page, cancellationToken));

    public Task<PagedResponse<ProductResponse>> ListByStockLevelAsync(
        int? min,
        int? max,
        PageQuery page,
        CancellationToken cancellationToken) =>
        GetOrLoadRequiredAsync(
            Invariant($"products:stock:{min?.ToString(CultureInfo.InvariantCulture) ?? "*"}:{max?.ToString(CultureInfo.InvariantCulture) ?? "*"}:{PageKey(page)}"),
            () => inner.ListByStockLevelAsync(min, max, page, cancellationToken));

    private async Task<PagedResponse<ProductResponse>> GetOrLoadRequiredAsync(
        string key,
        Func<Task<PagedResponse<ProductResponse>>> load) =>
        (await GetOrLoadAsync<PagedResponse<ProductResponse>>(key, _options.ListTtl, async () => await load()))!;

    /// <remarks>
    /// The generation is read <em>before</em> the database. If a write commits in between, our (possibly older) result
    /// is stored under the previous generation, which nobody reads any more, so a stale value can never become visible.
    /// Missing products are not cached, so a product created later is found immediately.
    /// </remarks>
    private async Task<T?> GetOrLoadAsync<T>(string key, TimeSpan timeToLive, Func<Task<T?>> load)
        where T : class
    {
        var generation = await cache.GetGenerationAsync();
        if (generation is null)
        {
            return await load();
        }

        var versionedKey = Invariant($"g{generation}:{key}");
        var cached = await cache.GetAsync<T>(versionedKey);
        if (cached is not null)
        {
            return cached;
        }

        var value = await load();
        if (value is not null)
        {
            await cache.SetAsync(versionedKey, value, timeToLive);
        }

        return value;
    }

    private static string PageKey(PageQuery page) => Invariant($"p{page.Page}:s{page.PageSize}");

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
