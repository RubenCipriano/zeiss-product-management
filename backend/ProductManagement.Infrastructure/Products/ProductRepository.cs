using Microsoft.EntityFrameworkCore;
using ProductManagement.Application.Products.Abstractions;
using ProductManagement.Domain.Products;
using ProductManagement.Infrastructure.Caching;
using ProductManagement.Infrastructure.Persistence;

namespace ProductManagement.Infrastructure.Products;

internal sealed class ProductRepository(ProductManagementDbContext context, ICatalogCache cache) : IProductRepository
{
    public Task<Product?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        context.Products.SingleOrDefaultAsync(product => product.Id == id, cancellationToken);

    public void Add(Product product) => context.Products.Add(product);

    public void Remove(Product product) => context.Products.Remove(product);

    public async Task<bool> TryAdjustStockAsync(
        int id,
        int delta,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // UPDATE products SET stock_quantity = stock_quantity + @delta, updated_at = @now
        // WHERE id = @id AND stock_quantity >= @min AND stock_quantity <= @max
        //
        // PostgreSQL row-locks the product for the statement and re-checks the WHERE clause against the latest
        // committed row, so concurrent decrements serialize and can never drive stock below zero.
        var affectedRows = await context.Products
            .Where(product => product.Id == id)
            .Where(Product.CanAdjustStockBy(delta))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(product => product.StockQuantity, product => product.StockQuantity + delta)
                    .SetProperty(product => product.UpdatedAt, now),
                cancellationToken);

        if (affectedRows != 1)
        {
            return false;
        }

        await cache.InvalidateAsync();
        return true;
    }
}
