using ProductManagement.Core.Common.Results;
using ProductManagement.Core.Products.Contracts;

namespace ProductManagement.Core.Products;

/// <summary>
/// The product write operations the API calls: create, edit (PUT) and delete. Adding and removing stock is in
/// <see cref="IStockCommandService"/>.
/// </summary>
public interface IProductCommandService
{
    Task<Result<ProductResponse>> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken);

    /// <param name="expectedVersion">
    /// The version the client last saw (from the <c>If-Match</c> header). When supplied, the update is refused if the
    /// product has changed since. When <c>null</c>, the update applies on top of the current state.
    /// </param>
    Task<Result<ProductResponse>> UpdateAsync(
        int id,
        UpdateProductRequest request,
        uint? expectedVersion,
        CancellationToken cancellationToken);

    Task<Result> DeleteAsync(int id, uint? expectedVersion, CancellationToken cancellationToken);
}
