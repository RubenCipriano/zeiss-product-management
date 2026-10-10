namespace ProductManagement.Core.Products.Abstractions;

/// <summary>
/// What the write use cases need from the database: load a <see cref="Product"/> to change it, add or remove products
/// and stock ledger entries, and apply a stock change atomically. Changes to loaded products are saved by
/// <see cref="Common.Persistence.IUnitOfWork"/>.
/// </summary>
public interface IProductRepository
{
    Task<Product?> GetByIdAsync(int id, CancellationToken cancellationToken);

    void Add(Product product);

    void Remove(Product product);

    /// <summary>Adds a stock ledger entry, saved together with the product by the unit of work.</summary>
    void AddMovement(StockMovement movement);

    /// <summary>
    /// Applies <paramref name="adjustment"/> if the current stock is within its allowed range, and records the matching
    /// ledger entry. Atomic: the check, the change and the ledger entry are one SQL statement, so two simultaneous
    /// requests can never both take the last unit, and a change is never recorded without its entry. Runs right away;
    /// it does not wait for the unit of work.
    /// </summary>
    Task<StockAdjustmentOutcome> TryAdjustStockAsync(
        int productId,
        StockAdjustment adjustment,
        CancellationToken cancellationToken);
}

/// <summary>What happened to a requested stock change.</summary>
public enum StockAdjustmentOutcome
{
    /// <summary>
    /// The product does not exist or the stock rule was not met; nothing changed. (The default value.)
    /// </summary>
    NotApplied,

    /// <summary>The stock was changed and the ledger entry recorded.</summary>
    Applied,

    /// <summary>This request (same idempotency key, same change) was applied before; nothing changed now.</summary>
    AlreadyApplied,

    /// <summary>
    /// The idempotency key is already recorded on a different change (another product, quantity or operation): it was
    /// reused for a different request. Nothing changed.
    /// </summary>
    RequestKeyReused,
}
