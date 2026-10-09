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

    public void AddMovement(StockMovement movement) => context.StockMovements.Add(movement);

    public async Task<int?> TryAdjustStockAsync(
        int id,
        int delta,
        string performedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (Product.StockRangeAllowingAdjustment(delta) is not var (min, max))
        {
            return null;
        }

        var reason = (delta < 0 ? StockMovementReason.StockRemoved : StockMovementReason.StockAdded).ToString();

        // One statement (a data-modifying CTE): the conditional UPDATE and the ledger INSERT succeed or fail together.
        // PostgreSQL row-locks the product and re-checks the WHERE clause against the latest committed row, so
        // concurrent decrements serialise and can never drive stock below zero. If the condition fails, `updated` is
        // empty and nothing is inserted. All values are parameters (interpolation becomes DbParameters).
        var stockAfter = await context.Database
            .SqlQuery<int>($"""
                WITH updated AS (
                    UPDATE products
                    SET stock_quantity = stock_quantity + {delta}, updated_at = {now}
                    WHERE id = {id} AND stock_quantity BETWEEN {min} AND {max}
                    RETURNING id, stock_quantity
                )
                INSERT INTO stock_movements (product_id, quantity_change, stock_after, reason, performed_by, occurred_at)
                SELECT id, {delta}, stock_quantity, {reason}, {performedBy}, {now} FROM updated
                RETURNING stock_after AS "Value"
                """)
            .ToListAsync(cancellationToken);

        if (stockAfter.Count == 0)
        {
            return null;
        }

        await cache.InvalidateAsync();
        return stockAfter[0];
    }
}
