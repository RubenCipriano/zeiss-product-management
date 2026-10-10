using ProductManagement.Core.Products;
using ProductManagement.Core.Products.Abstractions;

namespace ProductManagement.Infrastructure.Caching;

/// <summary>
/// Wraps the real product repository (a decorator: same interface, adds one behaviour) to clear the affected cache
/// entries after a stock change.
/// </summary>
/// <remarks>
/// Stock changes run immediately in the database, not through the unit of work, so
/// <see cref="CacheInvalidatingUnitOfWork"/> never sees them; this class covers them. Every other call is passed
/// straight through.
/// </remarks>
internal class CacheInvalidatingProductRepository(IProductRepository inner, ICatalogCache cache)
    : IProductRepository
{
    public Task<Product?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        inner.GetByIdAsync(id, cancellationToken);

    public void Add(Product product) => inner.Add(product);

    public void Remove(Product product) => inner.Remove(product);

    public void AddMovement(StockMovement movement) => inner.AddMovement(movement);

    public async Task<StockAdjustmentOutcome> TryAdjustStockAsync(
        int productId,
        StockAdjustment adjustment,
        CancellationToken cancellationToken)
    {
        var outcome = await inner.TryAdjustStockAsync(productId, adjustment, cancellationToken);
        if (outcome == StockAdjustmentOutcome.Applied)
        {
            // Stock only: lists keep their cached membership and order, and read the new quantity with the rows.
            await cache.InvalidateAsync(CatalogChange.Stock, [productId]);
        }

        return outcome;
    }
}
