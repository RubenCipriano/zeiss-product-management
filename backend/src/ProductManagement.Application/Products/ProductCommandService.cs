using FluentValidation;
using Microsoft.Extensions.Logging;
using ProductManagement.Application.Categories.Abstractions;
using ProductManagement.Application.Common.Persistence;
using ProductManagement.Application.Common.Results;
using ProductManagement.Application.Common.Validation;
using ProductManagement.Application.Products.Abstractions;
using ProductManagement.Application.Products.Contracts;
using ProductManagement.Domain.Products;

namespace ProductManagement.Application.Products;

internal sealed partial class ProductCommandService(
    IProductRepository products,
    IProductQueries productQueries,
    ICategoryQueries categories,
    IProductIdGenerator idGenerator,
    IUnitOfWork unitOfWork,
    IValidator<CreateProductRequest> createValidator,
    IValidator<UpdateProductRequest> updateValidator,
    TimeProvider timeProvider,
    ILogger<ProductCommandService> logger) : IProductCommandService
{
    public async Task<Result<ProductResponse>> CreateAsync(
        CreateProductRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await createValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ToError();
        }

        var categoryId = request.CategoryId!.Value;
        if (!await categories.ExistsAsync(categoryId, cancellationToken))
        {
            return ProductErrors.CategoryNotFound(categoryId);
        }

        var id = await idGenerator.NextIdAsync(cancellationToken);
        var product = Product.Create(
            id,
            request.Name!,
            request.Description,
            request.Price!.Value,
            request.StockQuantity!.Value,
            categoryId,
            timeProvider.GetUtcNow());

        products.Add(product);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        LogProductCreated(product.Id, product.StockQuantity);
        return await LoadAsync(product.Id, cancellationToken);
    }

    public async Task<Result<ProductResponse>> UpdateAsync(
        int id,
        UpdateProductRequest request,
        uint? expectedVersion,
        CancellationToken cancellationToken)
    {
        var validation = await updateValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return validation.ToError();
        }

        var product = await products.GetByIdAsync(id, cancellationToken);
        if (product is null)
        {
            return ProductErrors.NotFound(id);
        }

        if (expectedVersion is not null && product.Version != expectedVersion)
        {
            return ProductErrors.VersionMismatch(id);
        }

        var categoryId = request.CategoryId!.Value;
        if (categoryId != product.CategoryId && !await categories.ExistsAsync(categoryId, cancellationToken))
        {
            return ProductErrors.CategoryNotFound(categoryId);
        }

        product.Update(
            request.Name!,
            request.Description,
            request.Price!.Value,
            request.StockQuantity!.Value,
            categoryId,
            timeProvider.GetUtcNow());

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // The row changed between our read and our write (the token we loaded no longer matches).
            LogConcurrencyConflict(id);
            return expectedVersion is null ? ProductErrors.ConcurrentModification(id) : ProductErrors.VersionMismatch(id);
        }

        LogProductUpdated(id);
        return await LoadAsync(id, cancellationToken);
    }

    public async Task<Result> DeleteAsync(int id, uint? expectedVersion, CancellationToken cancellationToken)
    {
        var product = await products.GetByIdAsync(id, cancellationToken);
        if (product is null)
        {
            return ProductErrors.NotFound(id);
        }

        if (expectedVersion is not null && product.Version != expectedVersion)
        {
            return ProductErrors.VersionMismatch(id);
        }

        products.Remove(product);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            LogConcurrencyConflict(id);
            return expectedVersion is null ? ProductErrors.ConcurrentModification(id) : ProductErrors.VersionMismatch(id);
        }

        LogProductDeleted(id);
        return Result.Success();
    }

    public Task<Result<ProductResponse>> DecrementStockAsync(int id, int quantity, CancellationToken cancellationToken) =>
        AdjustStockAsync(id, quantity, StockOperation.Remove, cancellationToken);

    public Task<Result<ProductResponse>> AddToStockAsync(int id, int quantity, CancellationToken cancellationToken) =>
        AdjustStockAsync(id, quantity, StockOperation.Add, cancellationToken);

    /// <summary>
    /// Stock changes skip load-modify-save on purpose: a single conditional UPDATE makes check-and-write atomic.
    /// Concurrent requests therefore never oversell and never fail with spurious concurrency conflicts.
    /// See docs/adr/0003-atomic-stock-updates.md.
    /// </summary>
    private async Task<Result<ProductResponse>> AdjustStockAsync(
        int id,
        int quantity,
        StockOperation operation,
        CancellationToken cancellationToken)
    {
        if (quantity is < 1 or > ProductRules.MaxStockQuantity)
        {
            return ProductErrors.InvalidQuantity();
        }

        var delta = operation == StockOperation.Remove ? -quantity : quantity;
        var adjusted = await products.TryAdjustStockAsync(id, delta, timeProvider.GetUtcNow(), cancellationToken);

        // The current state tells us both what to return on success and why it failed otherwise.
        var current = await productQueries.GetByIdAsync(id, cancellationToken);
        if (current is null)
        {
            return ProductErrors.NotFound(id);
        }

        if (!adjusted)
        {
            return operation == StockOperation.Remove
                ? ProductErrors.InsufficientStock(id, quantity, current.StockQuantity)
                : ProductErrors.StockLimitExceeded(id, quantity, current.StockQuantity);
        }

        LogStockAdjusted(id, delta, current.StockQuantity);
        return current;
    }

    private async Task<Result<ProductResponse>> LoadAsync(int id, CancellationToken cancellationToken)
    {
        // Re-read through the query side so every endpoint returns the exact same representation
        // (including the category name and the database-generated version).
        var response = await productQueries.GetByIdAsync(id, cancellationToken);
        return response is null ? ProductErrors.NotFound(id) : response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created product {ProductId} with {StockQuantity} unit(s) in stock")]
    private partial void LogProductCreated(int productId, int stockQuantity);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated product {ProductId}")]
    private partial void LogProductUpdated(int productId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted product {ProductId}")]
    private partial void LogProductDeleted(int productId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Adjusted stock of product {ProductId} by {Delta}; now {StockQuantity}")]
    private partial void LogStockAdjusted(int productId, int delta, int stockQuantity);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optimistic concurrency conflict on product {ProductId}")]
    private partial void LogConcurrencyConflict(int productId);

    private enum StockOperation
    {
        Remove,
        Add,
    }
}
