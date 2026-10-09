using ProductManagement.Application.Common.Paging;
using ProductManagement.Application.Common.Results;
using ProductManagement.Application.Products.Contracts;

namespace ProductManagement.Application.Products;

/// <summary>
/// Product read use cases.
/// </summary>
public interface IProductQueryService
{
    Task<Result<PagedResponse<ProductResponse>>> ListAsync(PageQuery query, CancellationToken cancellationToken);

    Task<Result<ProductResponse>> GetByIdAsync(int id, CancellationToken cancellationToken);

    Task<Result<PagedResponse<ProductResponse>>> SearchByNameAsync(
        ProductSearchQuery query,
        CancellationToken cancellationToken);

    Task<Result<PagedResponse<ProductResponse>>> ListByStockLevelAsync(
        StockLevelQuery query,
        CancellationToken cancellationToken);
}
