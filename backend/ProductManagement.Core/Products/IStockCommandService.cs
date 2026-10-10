using ProductManagement.Core.Common.Results;
using ProductManagement.Core.Products.Contracts;

namespace ProductManagement.Core.Products;

/// <summary>
/// The stock operations the API calls: remove units (a sale) or add units (a delivery). Each change is applied in one
/// atomic database statement (see <see cref="StockCommandService"/>).
/// </summary>
public interface IStockCommandService
{
    Task<Result<ProductResponse>> DecrementStockAsync(int id, int quantity, CancellationToken cancellationToken);

    Task<Result<ProductResponse>> AddToStockAsync(int id, int quantity, CancellationToken cancellationToken);
}
