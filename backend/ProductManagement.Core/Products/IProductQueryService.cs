using ProductManagement.Core.Common.Paging;
using ProductManagement.Core.Common.Results;
using ProductManagement.Core.Products.Contracts;

namespace ProductManagement.Core.Products;

/// <summary>
/// The product read operations the API calls: list, get one, search by name, filter by stock level, stock history.
/// </summary>
public interface IProductQueryService
{
    Task<Result<PagedResponse<ProductResponse>>> ListAsync(PageQuery query, CancellationToken cancellationToken);

    Task<Result<ProductResponse>> GetByIdAsync(int id, CancellationToken cancellationToken);

    Task<Result<PagedResponse<ProductResponse>>> SearchByNameAsync(
        ProductSearchQuery query,
        CancellationToken cancellationToken);

    Task<Result<PagedResponse<StockMovementResponse>>> ListStockMovementsAsync(
        int productId,
        PageQuery query,
        CancellationToken cancellationToken);

    Task<Result<PagedResponse<ProductResponse>>> ListByStockLevelAsync(
        StockLevelQuery query,
        CancellationToken cancellationToken);
}
