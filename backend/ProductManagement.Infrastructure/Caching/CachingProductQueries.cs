using System.Globalization;
using Microsoft.Extensions.Options;
using ProductManagement.Core.Common.Paging;
using ProductManagement.Core.Products.Abstractions;
using ProductManagement.Core.Products.Contracts;
using ProductManagement.Infrastructure.Products;

namespace ProductManagement.Infrastructure.Caching;

/// <summary>
/// Puts a cache in front of the product read queries: a decorator around <see cref="ProductQueries"/>. Core asks for
/// <see cref="IProductQueries"/> and gets this class, without knowing a cache exists. Without Redis the cache is a
/// pass-through (<see cref="NullCatalogCache"/>) and every read goes to the database.
/// </summary>
/// <remarks>
/// <para>
/// Cache-aside: look in the cache; on a miss, read the database and store the answer. Each key includes a version
/// made of the generations it depends on (<see cref="CacheScope"/>, explained in <see cref="ICatalogCache"/>), so a
/// write only invalidates what it affects: a stock change invalidates that product's details and history and the
/// stock-level lists, while the full list and searches stay cached.
/// </para>
/// <para>
/// Lists and searches cache only the expensive part: the total count (once per list, whatever the page) and the
/// page's ids. The rows themselves are re-read from the database by id every time, which is cheap, so the stock shown
/// is always current. Measured in ADR 0008 (redis cache).
/// </para>
/// <para>
/// Simultaneous misses for the same entry share one database query per replica (<see cref="SingleFlight"/>).
/// </para>
/// </remarks>
internal class CachingProductQueries(
    ProductQueries inner,
    ICatalogCache cache,
    SingleFlight singleFlight,
    IOptions<CacheOptions> options) : IProductQueries
{
    /// <summary>For entries that do not belong to one product (lists, searches).</summary>
    private const int NoProduct = 0;

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
        ListingPageAsync(new ProductListing.All(), "products:all", page, cancellationToken);

    public Task<PagedResponse<ProductResponse>> SearchByNameAsync(
        string nameFragment,
        PageQuery page,
        CancellationToken cancellationToken) =>
        ListingPageAsync(
            new ProductListing.NameContains(nameFragment),
            // The search is case-insensitive, so differently-cased terms share one entry.
            Invariant($"products:search:{nameFragment.ToUpperInvariant()}"),
            page,
            cancellationToken);

    // Which products match depends on their stock here, so the whole page is cached under the data generation, which
    // every write (stock changes included) increments.
    public async Task<PagedResponse<ProductResponse>> ListByStockLevelAsync(
        int? min,
        int? max,
        PageQuery page,
        CancellationToken cancellationToken) =>
        (await GetOrLoadAsync(
            CacheScope.Data,
            NoProduct,
            StockLevelKey(min, max, page),
            _options.ListTtl,
            async () => await inner.PageAsync(new ProductListing.StockBetween(min, max), page, cancellationToken)))!;

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

    /// <summary>
    /// A list or search page: the count and the page's ids from the cache, the rows from the database.
    /// </summary>
    private Task<PagedResponse<ProductResponse>> ListingPageAsync(
        ProductListing listing,
        string listKey,
        PageQuery page,
        CancellationToken cancellationToken) =>
        inner.PageAsync(
            page,
            () => CachedCountAsync(listing, listKey, cancellationToken),
            () => CachedPageIdsAsync(listing, listKey, page, cancellationToken),
            cancellationToken);

    private async Task<int> CachedCountAsync(
        ProductListing listing,
        string listKey,
        CancellationToken cancellationToken)
    {
        var count = await GetOrLoadAsync(
            CacheScope.Listing,
            NoProduct,
            $"{listKey}:count",
            _options.ListingTtl,
            async () => new CachedCount(await inner.CountAsync(listing, cancellationToken)));
        return count!.Value;
    }

    private async Task<int[]> CachedPageIdsAsync(
        ProductListing listing,
        string listKey,
        PageQuery page,
        CancellationToken cancellationToken)
    {
        var ids = await GetOrLoadAsync(
            CacheScope.Listing,
            NoProduct,
            $"{listKey}:ids:{PageKey(page)}",
            _options.ListingTtl,
            async () => await inner.PageIdsAsync(listing, page, cancellationToken));
        return ids!;
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

    /// <summary>
    /// <c>products:stock:{min}:{max}:{page}</c>, with <c>*</c> for an open bound.
    /// </summary>
    private static string StockLevelKey(int? min, int? max, PageQuery page)
    {
        var lower = min?.ToString(CultureInfo.InvariantCulture) ?? "*";
        var upper = max?.ToString(CultureInfo.InvariantCulture) ?? "*";
        return Invariant($"products:stock:{lower}:{upper}:{PageKey(page)}");
    }

    private static string PageKey(PageQuery page) => Invariant($"p{page.Page}:s{page.PageSize}");

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A count, wrapped in an object so it can be cached like any other entry.</summary>
internal record CachedCount(int Value);
