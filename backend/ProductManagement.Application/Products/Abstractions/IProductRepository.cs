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

    /// <summary>
    /// Atomically adds <paramref name="delta"/> (positive or negative) to the stock, but only if the result stays
    /// within <see cref="Product.CanAdjustStockBy"/>. Runs as a single conditional UPDATE, right away; it does not
    /// wait for the unit of work.
    /// </summary>
    /// <returns><c>true</c> if the stock changed; <c>false</c> if the product does not exist or the rule was not met.</returns>
    Task<bool> TryAdjustStockAsync(int id, int delta, DateTimeOffset now, CancellationToken cancellationToken);
}
