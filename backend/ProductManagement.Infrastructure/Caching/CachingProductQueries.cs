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
/// <para>
/// Each entry is versioned by what it depends on (<see cref="CacheScope"/>), so a write invalidates only what it
/// affects. A stock change invalidates that product's details and history and the stock-level lists; the full
/// list and searches stay cached.
/// </para>
/// <para>
/// Lists and searches are cached in three parts: the total count (once per list, whatever the page), the page's ids
/// (per page) and the rows, read from the database every time by primary key (cheap: ~1 ms for a page). The stock
/// shown is therefore always current, even though the expensive part (a count and an index walk over up to
/// hundreds of thousands of rows) is cached across stock changes.
/// </para>
/// <para>
/// Concurrent misses for the same entry are collapsed into one query per replica (<see cref="SingleFlight"/>).
/// </para>
/// </remarks>
internal sealed class CachingProductQueries(
    ProductQueries inner,
    ICatalogCache cache,
    SingleFlight singleFlight,
    IOptions<CacheOptions> options) : IProductQueries
{
    private readonly CacheOptions _options = options.Value;

    public Task<ProductResponse?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        GetOrLoadAsync(
            CacheScope.Product,
            id,
            Invariant($"product:{id}"),
            _options.ProductTtl,
            () => inner.GetByIdAsync(id, cancellationToken));

    public Task<ProductResponse?> FindCreatedByRequestAsync(string requestKey, CancellationToken cancellationToken) =>
        inner.FindCreatedByRequestAsync(requestKey, cancellationToken);

    public Task<PagedResponse<ProductResponse>> ListAsync(PageQuery page, CancellationToken cancellationToken) =>
        ListingPageAsync(
            "products:all",
            page,
            () => inner.CountListAsync(cancellationToken),
            () => inner.FindListPageIdsAsync(page, cancellationToken),
            cancellationToken);

    public Task<PagedResponse<ProductResponse>> SearchByNameAsync(
        string nameFragment,
        PageQuery page,
        CancellationToken cancellationToken) =>
        ListingPageAsync(
            // The search is case-insensitive, so differently-cased terms share one entry.
            Invariant($"products:search:{nameFragment.ToUpperInvariant()}"),
            page,
            () => inner.CountSearchAsync(nameFragment, cancellationToken),
            () => inner.FindSearchPageIdsAsync(nameFragment, page, cancellationToken),
            cancellationToken);

    // Which products match depends on their stock here, so the whole page follows the data generation.
    public async Task<PagedResponse<ProductResponse>> ListByStockLevelAsync(
        int? min,
        int? max,
        PageQuery page,
        CancellationToken cancellationToken) =>
        (await GetOrLoadAsync(
            CacheScope.Data,
            0,
            Invariant($"products:stock:{min?.ToString(CultureInfo.InvariantCulture) ?? "*"}:{max?.ToString(CultureInfo.InvariantCulture) ?? "*"}:{PageKey(page)}"),
            _options.ListTtl,
            async () => await inner.ListByStockLevelAsync(min, max, page, cancellationToken)))!;

    public Task<PagedResponse<StockMovementResponse>?> ListStockMovementsAsync(
        int productId,
        PageQuery page,
        CancellationToken cancellationToken) =>
        GetOrLoadAsync(
            CacheScope.Product,
            productId,
            Invariant($"product:{productId}:movements:{PageKey(page)}"),
            _options.ListTtl,
            () => inner.ListStockMovementsAsync(productId, page, cancellationToken));

    /// <summary>A list or search page: the count and the page's ids from the cache, the rows from the database.</summary>
    private async Task<PagedResponse<ProductResponse>> ListingPageAsync(
        string listKey,
        PageQuery page,
        Func<Task<int>> count,
        Func<Task<int[]>> findIds,
        CancellationToken cancellationToken)
    {
        var total = await GetOrLoadAsync(
            CacheScope.Listing,
            0,
            $"{listKey}:count",
            _options.ListingTtl,
            async () => new CachedCount(await count()));
        int[] ids = total!.Value <= page.Skip
            ? []
            : (await GetOrLoadAsync(
                CacheScope.Listing,
                0,
                $"{listKey}:ids:{PageKey(page)}",
                _options.ListingTtl,
                async () => await findIds()))!;
        return await inner.LoadPageAsync(new ProductPageIds(ids, total.Value), page, cancellationToken);
    }

    /// <remarks>
    /// The version is read <em>before</em> the database. If a write commits in between, our (possibly older) result is
    /// stored under the previous version, which nobody reads any more, so a stale value can never become visible.
    /// Missing products are not cached, so a product created later is found immediately.
    /// </remarks>
    private async Task<T?> GetOrLoadAsync<T>(
        CacheScope scope,
        int productId,
        string key,
        TimeSpan timeToLive,
        Func<Task<T?>> load)
        where T : class
    {
        var version = await cache.GetVersionAsync(scope, productId);
        if (version is null)
        {
            return await load();
        }

        var versionedKey = Invariant($"v{version}:{key}");
        return await cache.GetAsync<T>(versionedKey)
            ?? await singleFlight.RunAsync(versionedKey, async () =>
            {
                var value = await load();
                if (value is not null)
                {
                    await cache.SetAsync(versionedKey, value, timeToLive);
                }

                return value;
            });
    }

    private static string PageKey(PageQuery page) => Invariant($"p{page.Page}:s{page.PageSize}");

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A count, boxed so it can be cached like any other entry.</summary>
internal sealed record CachedCount(int Value);
