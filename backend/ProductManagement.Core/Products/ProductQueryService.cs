using FluentValidation;
using ProductManagement.Core.Common.Paging;
using ProductManagement.Core.Common.Results;
using ProductManagement.Core.Common.Validation;
using ProductManagement.Core.Products.Abstractions;
using ProductManagement.Core.Products.Contracts;

namespace ProductManagement.Core.Products;

/// <summary>
/// Handles product read requests (list, get one, search, stock level, stock history): validates the query, then reads
/// the data through <see cref="IProductQueries"/> (which is cached when Redis is configured).
/// </summary>
internal class ProductQueryService(
    IProductQueries products,
    IValidator<PageQuery> pageValidator,
    IValidator<ProductSearchQuery> searchValidator,
    IValidator<StockLevelQuery> stockLevelValidator) : IProductQueryService
{
    public async Task<Result<PagedResponse<ProductResponse>>> ListAsync(
        PageQuery query,
        CancellationToken cancellationToken)
    {
        var validation = await pageValidator.ValidateAsync(query, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ToError();
        }

        return await products.ListAsync(query, cancellationToken);
    }

    public async Task<Result<ProductResponse>> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        var product = await products.GetByIdAsync(id, cancellationToken);
        return product is null ? ProductErrors.NotFound(id) : product;
    }

    public async Task<Result<PagedResponse<ProductResponse>>> SearchByNameAsync(
        ProductSearchQuery query,
        CancellationToken cancellationToken)
    {
        var validation = await searchValidator.ValidateAsync(query, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ToError();
        }

        return await products.SearchByNameAsync(query.Name!.Trim(), query, cancellationToken);
    }

    public async Task<Result<PagedResponse<StockMovementResponse>>> ListStockMovementsAsync(
        int productId,
        PageQuery query,
        CancellationToken cancellationToken)
    {
        var validation = await pageValidator.ValidateAsync(query, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ToError();
        }

        var movements = await products.ListStockMovementsAsync(productId, query, cancellationToken);
        return movements is null ? ProductErrors.NotFound(productId) : movements;
    }

    public async Task<Result<PagedResponse<ProductResponse>>> ListByStockLevelAsync(
        StockLevelQuery query,
        CancellationToken cancellationToken)
    {
        var validation = await stockLevelValidator.ValidateAsync(query, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ToError();
        }

        return await products.ListByStockLevelAsync(query.Min, query.Max, query, cancellationToken);
    }
}
