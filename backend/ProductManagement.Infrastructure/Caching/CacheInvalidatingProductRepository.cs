using ProductManagement.Application.Products.Abstractions;
using ProductManagement.Domain.Products;

namespace ProductManagement.Infrastructure.Caching;

/// <summary>
/// Invalidates the read cache after an atomic stock change. That change runs right away, outside the unit of work, so
/// <see cref="CacheInvalidatingUnitOfWork"/> never sees it.
/// </summary>
internal sealed class CacheInvalidatingProductRepository(IProductRepository inner, ICatalogCache cache)
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
