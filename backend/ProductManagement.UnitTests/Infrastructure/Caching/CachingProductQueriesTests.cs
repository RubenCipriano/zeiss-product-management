using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProductManagement.Core.Common.Paging;
using ProductManagement.Core.Products.Contracts;
using ProductManagement.Infrastructure.Caching;
using ProductManagement.Infrastructure.Persistence;
using ProductManagement.Infrastructure.Products;

namespace ProductManagement.UnitTests.Infrastructure.Caching;

/// <summary>
/// Which key and version each read is cached under. Every case here is answered from the fake cache, so the database
/// behind <see cref="ProductQueries"/> is never reached (it does not exist); cache misses are covered by the
/// integration tests.
/// </summary>
public class CachingProductQueriesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly ProductManagementDbContext _context = new(
        new DbContextOptionsBuilder<ProductManagementDbContext>().UseNpgsql("Host=unused").Options);

    private readonly FakeCatalogCache _cache = new();
    private readonly CachingProductQueries _queries;

    public CachingProductQueriesTests() =>
        _queries = new CachingProductQueries(
            new ProductQueries(_context),
            _cache,
            new SingleFlight(),
            Options.Create(new CacheOptions()));

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task AProduct_IsCachedUnderItsOwnVersion()
    {
        _cache.Versions[(CacheScope.Product, 100_001)] = "7.42";
        _cache.Entries["v7.42:product:100001"] = Product(100_001);

        var product = await _queries.GetByIdAsync(100_001, TestContext.Current.CancellationToken);

        product!.Id.ShouldBe(100_001);
        _cache.Reads.ShouldBe(["v7.42:product:100001"]);
    }

    [Fact]
    public async Task AStockHistoryPage_FollowsItsProductsVersion()
    {
        _cache.Versions[(CacheScope.Product, 100_001)] = "3.9";
        _cache.Entries["v3.9:product:100001:movements:p2:s5"] = new PagedResponse<StockMovementResponse>([], 2, 5, 0);

        await _queries.ListStockMovementsAsync(100_001, new PageQuery { Page = 2, PageSize = 5 }, TestContext.Current.CancellationToken);

        _cache.Reads.ShouldBe(["v3.9:product:100001:movements:p2:s5"]);
    }

    [Fact]
    public async Task AStockLevelPage_FollowsTheDataVersion()
    {
        _cache.Versions[(CacheScope.Data, 0)] = "11";
        _cache.Entries["v11:products:stock:*:10:p1:s20"] = new PagedResponse<ProductResponse>([Product(100_001)], 1, 20, 1);

        var page = await _queries.ListByStockLevelAsync(null, 10, new PageQuery(), TestContext.Current.CancellationToken);

        page.Items.Single().Id.ShouldBe(100_001);
        _cache.Reads.ShouldBe(["v11:products:stock:*:10:p1:s20"]);
    }

    [Fact]
    public async Task ASearch_CachesCountAndIdsUnderTheListingVersion_WhateverTheCaseOfTheTerm()
    {
        _cache.Versions[(CacheScope.Listing, 0)] = "5";
        _cache.Entries["v5:products:search:LENS:count"] = new CachedCount(3);
        _cache.Entries["v5:products:search:LENS:ids:p1:s20"] = Array.Empty<int>();

        var page = await _queries.SearchByNameAsync("Lens", new PageQuery(), TestContext.Current.CancellationToken);

        page.TotalCount.ShouldBe(3);
        _cache.Reads.ShouldBe(["v5:products:search:LENS:count", "v5:products:search:LENS:ids:p1:s20"]);
    }

    [Fact]
    public async Task APageBeyondTheEnd_IsAnsweredFromTheCachedCountAlone()
    {
        _cache.Versions[(CacheScope.Listing, 0)] = "5";
        _cache.Entries["v5:products:all:count"] = new CachedCount(20);

        var page = await _queries.ListAsync(new PageQuery { Page = 2, PageSize = 20 }, TestContext.Current.CancellationToken);

        page.Items.ShouldBeEmpty();
        page.TotalCount.ShouldBe(20);
        page.Page.ShouldBe(2);
        _cache.Reads.ShouldBe(["v5:products:all:count"]);
    }

    private static ProductResponse Product(int id) =>
        new(id, "Lens", null, 10m, 5, 1, "Lenses", Now, Now, Version: 1);
}
