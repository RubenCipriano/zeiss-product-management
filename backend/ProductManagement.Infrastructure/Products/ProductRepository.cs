using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProductManagement.Application.Products.Abstractions;
using ProductManagement.Domain.Products;
using ProductManagement.Infrastructure.Persistence;

namespace ProductManagement.Infrastructure.Products;

internal sealed class ProductRepository(ProductManagementDbContext context) : IProductRepository
{
    public Task<Product?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        context.Products.SingleOrDefaultAsync(product => product.Id == id, cancellationToken);

    public void Add(Product product) => context.Products.Add(product);

    public void Remove(Product product) => context.Products.Remove(product);

    public void AddMovement(StockMovement movement) => context.StockMovements.Add(movement);

    /// <remarks>
    /// The rule and the ledger fields come from <see cref="StockAdjustment"/>; this method only maps them to SQL.
    /// </remarks>
    public async Task<StockAdjustmentOutcome> TryAdjustStockAsync(
        int productId,
        StockAdjustment adjustment,
        CancellationToken cancellationToken)
    {
        var delta = adjustment.Delta;
        var reason = adjustment.Reason.ToString();

        // One statement (a data-modifying CTE): the conditional UPDATE and the ledger INSERT succeed or fail together.
        // PostgreSQL row-locks the product and re-checks the WHERE clause against the latest committed row, so
        // concurrent decrements serialise and can never drive stock below zero. If the condition fails, `updated` is
        // empty and nothing is inserted. All values are parameters (interpolation becomes DbParameters).
        // The request key goes into the ledger row under a unique index: if this request was already applied, the
        // INSERT fails and PostgreSQL rolls back the UPDATE with it.
        List<int> stockAfter;
        try
        {
            stockAfter = await context.Database
                .SqlQuery<int>($"""
                    WITH updated AS (
                        UPDATE products
                        SET stock_quantity = stock_quantity + {delta}, updated_at = {adjustment.OccurredAt}
                        WHERE id = {productId}
                          AND stock_quantity BETWEEN {adjustment.MinCurrentStock} AND {adjustment.MaxCurrentStock}
                        RETURNING id, stock_quantity
                    )
                    INSERT INTO stock_movements
                        (product_id, quantity_change, stock_after, reason, performed_by, occurred_at, request_key)
                    SELECT id, {delta}, stock_quantity, {reason}, {adjustment.PerformedBy}, {adjustment.OccurredAt}, {adjustment.RequestKey}
                    FROM updated
                    RETURNING stock_after AS "Value"
                    """)
                .ToListAsync(cancellationToken);
        }
        catch (PostgresException exception) when (PostgresErrors.IsDuplicateRequestKey(exception))
        {
            return StockAdjustmentOutcome.AlreadyApplied;
        }

        return stockAfter.Count == 0 ? StockAdjustmentOutcome.NotApplied : StockAdjustmentOutcome.Applied;
    }
}
