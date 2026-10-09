using ProductManagement.Application.Common.Results;
using ProductManagement.Application.Products.Contracts;

namespace ProductManagement.Application.Products;

/// <summary>
/// Product write use cases (create, edit, delete). Stock movements are in <see cref="IStockCommandService"/>.
/// </summary>
public interface IProductCommandService
{
    Task<Result<ProductResponse>> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken);

    /// <param name="expectedVersion">
    /// Version the client last saw (from <c>If-Match</c>). When supplied, the update is rejected if the product
    /// has changed since. When <c>null</c>, the update applies on top of the current state.
    /// </param>
    Task<Result<ProductResponse>> UpdateAsync(
        int id,
        UpdateProductRequest request,
        uint? expectedVersion,
        CancellationToken cancellationToken);

    Task<Result> DeleteAsync(int id, uint? expectedVersion, CancellationToken cancellationToken);
}
