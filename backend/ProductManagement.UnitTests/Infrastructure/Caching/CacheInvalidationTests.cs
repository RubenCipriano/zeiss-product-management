using Microsoft.EntityFrameworkCore;
using NSubstitute;
using ProductManagement.Core.Common.Persistence;
using ProductManagement.Core.Products;
using ProductManagement.Core.Products.Abstractions;
using ProductManagement.Infrastructure.Caching;
using ProductManagement.Infrastructure.Persistence;

namespace ProductManagement.UnitTests.Infrastructure.Caching;

/// <summary>The write-side decorators: what each write invalidates, and that it happens only after the commit.</summary>
public class CacheInvalidationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly ProductManagementDbContext _context = new(
        new DbContextOptionsBuilder<ProductManagementDbContext>().UseNpgsql("Host=unused").Options);

    private readonly FakeCatalogCache _cache = new();

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task SavingProductChanges_InvalidatesThoseProductsAfterTheCommit()
    {
        _context.Products.Add(Product.Create(100_001, "Lens", null, 10m, 5, 1, Now));
        _context.Products.Attach(Product.Create(100_002, "Frame", null, 10m, 5, 1, Now)); // unchanged
        var inner = Substitute.For<IUnitOfWork>();
        inner.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            // Committing makes every entry Unchanged; the decorator must have looked before.
            _context.ChangeTracker.AcceptAllChanges();
            _cache.Invalidations.ShouldBeEmpty();
            return Task.CompletedTask;
        });

        await new CacheInvalidatingUnitOfWork(inner, _context, _cache).SaveChangesAsync(TestContext.Current.CancellationToken);

        var invalidation = _cache.Invalidations.ShouldHaveSingleItem();
        invalidation.Change.ShouldBe(CatalogChange.Products);
        invalidation.ProductIds.ShouldBe([100_001]);
    }

    [Fact]
    public async Task AFailedSave_InvalidatesNothing()
    {
        _context.Products.Add(Product.Create(100_001, "Lens", null, 10m, 5, 1, Now));
        var inner = Substitute.For<IUnitOfWork>();
        inner.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new ConcurrencyConflictException()));

        await Should.ThrowAsync<ConcurrencyConflictException>(() =>
            new CacheInvalidatingUnitOfWork(inner, _context, _cache).SaveChangesAsync(TestContext.Current.CancellationToken));

        _cache.Invalidations.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(StockAdjustmentOutcome.Applied, true)]
    [InlineData(StockAdjustmentOutcome.NotApplied, false)]
    [InlineData(StockAdjustmentOutcome.AlreadyApplied, false)] // applied (and invalidated) by the first execution
    public async Task AStockAdjustment_InvalidatesOnlyThatProductsStock_WhenItChangedSomething(
        StockAdjustmentOutcome outcome,
        bool invalidated)
    {
        var adjustment = StockAdjustment.Plan(-1, "maria", null, Now)!;
        var inner = Substitute.For<IProductRepository>();
        inner.TryAdjustStockAsync(100_001, adjustment, Arg.Any<CancellationToken>()).Returns(outcome);

        var result = await new CacheInvalidatingProductRepository(inner, _cache)
            .TryAdjustStockAsync(100_001, adjustment, TestContext.Current.CancellationToken);

        result.ShouldBe(outcome);
        if (invalidated)
        {
            var invalidation = _cache.Invalidations.ShouldHaveSingleItem();
            invalidation.Change.ShouldBe(CatalogChange.Stock);
            invalidation.ProductIds.ShouldBe([100_001]);
        }
        else
        {
            _cache.Invalidations.ShouldBeEmpty();
        }
    }
}
