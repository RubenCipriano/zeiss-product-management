using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProductManagement.Application.Products.Abstractions;
using ProductManagement.Domain.Products;
using ProductManagement.Infrastructure.Caching;
using ProductManagement.Infrastructure.Persistence;
using ProductManagement.Infrastructure.Persistence.Configurations;

namespace ProductManagement.Infrastructure.Products;

internal sealed class ProductRepository(ProductManagementDbContext context, ICatalogCache cache) : IProductRepository
{
    public Task<Product?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        context.Products.SingleOrDefaultAsync(product => product.Id == id, cancellationToken);

    public void Add(Product product) => context.Products.Add(product);

    public void Remove(Product product) => context.Products.Remove(product);

    public void AddMovement(StockMovement movement) => context.StockMovements.Add(movement);

    public async Task<StockAdjustmentOutcome> TryAdjustStockAsync(
        int id,
        int delta,
        string performedBy,
        string? requestKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (Product.StockRangeAllowingAdjustment(delta) is not var (min, max))
        {
            return StockAdjustmentOutcome.NotApplied;
        }

        var reason = (delta < 0 ? StockMovementReason.StockRemoved : StockMovementReason.StockAdded).ToString();

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
                    SET stock_quantity = stock_quantity + {delta}, updated_at = {now}
                    WHERE id = {id} AND stock_quantity BETWEEN {min} AND {max}
                    RETURNING id, stock_quantity
                )
                INSERT INTO stock_movements
                    (product_id, quantity_change, stock_after, reason, performed_by, occurred_at, request_key)
                SELECT id, {delta}, stock_quantity, {reason}, {performedBy}, {now}, {requestKey} FROM updated
                RETURNING stock_after AS "Value"
                """)
            .ToListAsync(cancellationToken);
        }
        catch (PostgresException exception) when (IsDuplicateRequest(exception))
        {
            return StockAdjustmentOutcome.AlreadyApplied;
        }

        if (stockAfter.Count == 0)
        {
            return StockAdjustmentOutcome.NotApplied;
        }

        // Stock only: lists keep their cached membership and order, and read the new quantity with the rows.
        await cache.InvalidateAsync(CatalogChange.Stock, [id]);
        return StockAdjustmentOutcome.Applied;
    }

    internal static bool IsDuplicateRequest(PostgresException exception) =>
        exception is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: StockMovementConfiguration.RequestKeyIndexName };
}
