using ProductManagement.Domain.Products;

namespace ProductManagement.Application.Products.Abstractions;

/// <summary>
/// Write-side access to the <see cref="Product"/> aggregate. Entities returned here are change-tracked;
/// modifications are persisted by <see cref="Common.Persistence.IUnitOfWork"/>.
/// </summary>
public interface IProductRepository
{
    Task<Product?> GetByIdAsync(int id, CancellationToken cancellationToken);

    void Add(Product product);

    void Remove(Product product);

    /// <summary>Adds a stock ledger entry, saved together with the product by the unit of work.</summary>
    void AddMovement(StockMovement movement);

    /// <summary>
    /// Atomically applies <paramref name="adjustment"/> if the current stock is within its allowed range, and records
    /// the matching ledger entry. Change and ledger entry are a single SQL statement, so one never happens without the
    /// other. Runs right away; it does not wait for the unit of work.
    /// </summary>
    Task<StockAdjustmentOutcome> TryAdjustStockAsync(
        int productId,
        StockAdjustment adjustment,
        CancellationToken cancellationToken);
}

public enum StockAdjustmentOutcome
{
    /// <summary>The product does not exist or the stock rule was not met; nothing changed. (The default value.)</summary>
    NotApplied,

    Applied,

    /// <summary>This request (same idempotency key) was applied before; nothing changed now.</summary>
    AlreadyApplied,
}
