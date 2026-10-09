using ProductManagement.Application.Common.Results;
using ProductManagement.Application.Products.Contracts;

namespace ProductManagement.Application.Products;

/// <summary>
/// Stock use cases: deliveries and sales, applied atomically without loading the product.
/// </summary>
public interface IStockCommandService
{
    Task<Result<ProductResponse>> DecrementStockAsync(int id, int quantity, CancellationToken cancellationToken);

    Task<Result<ProductResponse>> AddToStockAsync(int id, int quantity, CancellationToken cancellationToken);
}
