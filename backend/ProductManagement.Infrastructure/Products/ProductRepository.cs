using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProductManagement.Core.Products;
using ProductManagement.Core.Products.Abstractions;
using ProductManagement.Infrastructure.Persistence;

namespace ProductManagement.Infrastructure.Products;

/// <summary>
/// Saves products and stock ledger entries with EF Core, and applies stock changes with one hand-written SQL
/// statement (<see cref="TryAdjustStockAsync"/>).
/// </summary>
internal class ProductRepository(ProductManagementDbContext context) : IProductRepository
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

        // One SQL statement (WITH updated AS (UPDATE ...) INSERT ...), so the stock change and its ledger entry succeed
        // or fail together:
        // - The UPDATE only applies if the stock is in the allowed range. PostgreSQL locks the row and re-checks that
        //   condition against the latest committed value, so simultaneous removals queue up and can never take the
        //   stock below zero. If the condition fails, `updated` is empty and nothing is inserted.
        // - The INSERT writes the request's idempotency key under a unique index: if the key was already used, the
        //   INSERT fails and PostgreSQL rolls back the UPDATE with it.
        // All {values} become SQL parameters (not pasted into the SQL text), so this is safe from SQL injection.
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
                    SELECT id, {delta}, stock_quantity, {reason},
                           {adjustment.PerformedBy}, {adjustment.OccurredAt}, {adjustment.RequestKey}
                    FROM updated
                    RETURNING stock_after AS "Value"
                    """)
                .ToListAsync(cancellationToken);
        }
        catch (PostgresException exception) when (PostgresErrors.IsDuplicateRequestKey(exception))
        {
            return await IsRecordedAsync(productId, adjustment, cancellationToken)
                ? StockAdjustmentOutcome.AlreadyApplied
                : StockAdjustmentOutcome.RequestKeyReused;
        }

        return stockAfter.Count == 0 ? StockAdjustmentOutcome.NotApplied : StockAdjustmentOutcome.Applied;
    }

    /// <summary>
    /// Whether the ledger entry holding the adjustment's request key records this very change. Only then was this
    /// request applied before; otherwise the key was reused for a different one (the idempotency store, which would
    /// have refused it, had already forgotten the key).
    /// </summary>
    private Task<bool> IsRecordedAsync(
        int productId,
        StockAdjustment adjustment,
        CancellationToken cancellationToken) =>
        context.StockMovements.AnyAsync(
            movement => movement.RequestKey == adjustment.RequestKey
                && movement.ProductId == productId
                && movement.QuantityChange == adjustment.Delta
                && movement.Reason == adjustment.Reason,
            cancellationToken);
}
