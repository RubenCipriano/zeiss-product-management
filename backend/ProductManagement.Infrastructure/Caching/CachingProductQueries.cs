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
/// <remarks>
/// Lists and searches are cached in two parts. Which products are on the page and how many match (the expensive
/// part: a count and an index walk over up to hundreds of thousands of rows) is cached under the
/// <see cref="CacheGeneration.Listing"/> generation, which stock changes do not move. The rows themselves (cheap: a
/// primary-key lookup of one page) are read from the database every time, so the stock shown is always current.
/// Under a constant stream of stock changes, lists stay cached and exact.
/// </remarks>
internal sealed class CachingProductQueries(
    ProductQueries inner,
    ICatalogCache cache,
    IOptions<CacheOptions> options) : IProductQueries
{
    private readonly CacheOptions _options = options.Value;

    public Task<ProductResponse?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        GetOrLoadAsync(
            CacheGeneration.Data,
            Invariant($"product:{id}"),
            _options.ProductTtl,
            () => inner.GetByIdAsync(id, cancellationToken));

    public async Task<PagedResponse<ProductResponse>> ListAsync(PageQuery page, CancellationToken cancellationToken)
    {
        var pageIds = await GetOrLoadAsync(
            CacheGeneration.Listing,
            Invariant($"products:all:ids:{PageKey(page)}"),
            _options.ListTtl,
            async () => await inner.FindListPageAsync(page, cancellationToken));
        return await inner.LoadPageAsync(pageIds!, page, cancellationToken);
    }

    public async Task<PagedResponse<ProductResponse>> SearchByNameAsync(
        string nameFragment,
        PageQuery page,
        CancellationToken cancellationToken)
    {
        var pageIds = await GetOrLoadAsync(
            CacheGeneration.Listing,
            // The search is case-insensitive, so differently-cased terms share one entry.
            Invariant($"products:search:ids:{nameFragment.ToUpperInvariant()}:{PageKey(page)}"),
            _options.ListTtl,
            async () => await inner.FindSearchPageAsync(nameFragment, page, cancellationToken));
        return await inner.LoadPageAsync(pageIds!, page, cancellationToken);
    }

    // Which products match depends on their stock here, so the whole page follows the data generation.
    public async Task<PagedResponse<ProductResponse>> ListByStockLevelAsync(
        int? min,
        int? max,
        PageQuery page,
        CancellationToken cancellationToken) =>
        (await GetOrLoadAsync(
            CacheGeneration.Data,
            Invariant($"products:stock:{min?.ToString(CultureInfo.InvariantCulture) ?? "*"}:{max?.ToString(CultureInfo.InvariantCulture) ?? "*"}:{PageKey(page)}"),
            _options.ListTtl,
            async () => await inner.ListByStockLevelAsync(min, max, page, cancellationToken)))!;

    public Task<PagedResponse<StockMovementResponse>?> ListStockMovementsAsync(
        int productId,
        PageQuery page,
        CancellationToken cancellationToken) =>
        GetOrLoadAsync(
            CacheGeneration.Data,
            Invariant($"product:{productId}:movements:{PageKey(page)}"),
            _options.ListTtl,
            () => inner.ListStockMovementsAsync(productId, page, cancellationToken));

    /// <remarks>
    /// The generation is read <em>before</em> the database. If a write commits in between, our (possibly older) result
    /// is stored under the previous generation, which nobody reads any more, so a stale value can never become visible.
    /// Missing products are not cached, so a product created later is found immediately.
    /// </remarks>
    private async Task<T?> GetOrLoadAsync<T>(
        CacheGeneration generationKind,
        string key,
        TimeSpan timeToLive,
        Func<Task<T?>> load)
        where T : class
    {
        var generation = await cache.GetGenerationAsync(generationKind);
        if (generation is null)
        {
            return await load();
        }

        var prefix = generationKind == CacheGeneration.Listing ? "l" : "g";
        var versionedKey = Invariant($"{prefix}{generation}:{key}");
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
