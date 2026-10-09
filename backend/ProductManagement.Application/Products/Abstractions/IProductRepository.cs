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
    /// Atomically adds <paramref name="delta"/> (positive or negative) to the stock, but only if the result stays
    /// within <see cref="Product.StockRangeAllowingAdjustment"/>, and records the matching
    /// <see cref="StockMovement"/>. Change and ledger entry are a single SQL statement, so one never happens without
    /// the other. Runs right away; it does not wait for the unit of work.
    /// </summary>
    /// <returns>The stock after the change, or <c>null</c> if the product does not exist or the rule was not met.</returns>
    Task<int?> TryAdjustStockAsync(
        int id,
        int delta,
        string performedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
