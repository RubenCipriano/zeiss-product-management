using ProductManagement.Application.Common.Paging;
using ProductManagement.Application.Products.Contracts;

namespace ProductManagement.Application.Products.Abstractions;

/// <summary>
/// Read-side access to products. Implementations project directly to response models with no-tracking queries,
/// so reads never load or track full entities.
/// </summary>
public interface IProductQueries
{
    Task<ProductResponse?> GetByIdAsync(int id, CancellationToken cancellationToken);

    Task<PagedResponse<ProductResponse>> ListAsync(PageQuery page, CancellationToken cancellationToken);

    Task<PagedResponse<ProductResponse>> SearchByNameAsync(
        string nameFragment,
        PageQuery page,
        CancellationToken cancellationToken);

    Task<PagedResponse<ProductResponse>> ListByStockLevelAsync(
        int? min,
        int? max,
        PageQuery page,
        CancellationToken cancellationToken);
}
