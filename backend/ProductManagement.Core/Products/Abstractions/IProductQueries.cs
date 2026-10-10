using ProductManagement.Core.Common.Paging;
using ProductManagement.Core.Products.Contracts;

namespace ProductManagement.Core.Products.Abstractions;

/// <summary>
/// What the read use cases need from the database: products and stock history, already shaped as API responses (no
/// full entity is loaded). Implemented in Infrastructure by <c>ProductQueries</c>, behind a cache
/// (<c>CachingProductQueries</c>).
/// </summary>
public interface IProductQueries
{
    Task<ProductResponse?> GetByIdAsync(int id, CancellationToken cancellationToken);

    Task<PagedResponse<ProductResponse>> ListAsync(PageQuery page, CancellationToken cancellationToken);

    Task<PagedResponse<ProductResponse>> SearchByNameAsync(
        string nameFragment,
        PageQuery page,
        CancellationToken cancellationToken);

    /// <summary>A product's stock history, newest first; <c>null</c> when the product does not exist.</summary>
    Task<PagedResponse<StockMovementResponse>?> ListStockMovementsAsync(
        int productId,
        PageQuery page,
        CancellationToken cancellationToken);

    Task<PagedResponse<ProductResponse>> ListByStockLevelAsync(
        int? min,
        int? max,
        PageQuery page,
        CancellationToken cancellationToken);

    /// <summary>The product created by the request with this idempotency key, if any.</summary>
    Task<ProductResponse?> FindCreatedByRequestAsync(string requestKey, CancellationToken cancellationToken);
}
