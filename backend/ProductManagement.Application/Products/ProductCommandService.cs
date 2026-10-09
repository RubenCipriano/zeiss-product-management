using FluentValidation;
using Microsoft.Extensions.Logging;
using ProductManagement.Application.Categories.Abstractions;
using ProductManagement.Application.Common.Persistence;
using ProductManagement.Application.Common.Requests;
using ProductManagement.Application.Common.Results;
using ProductManagement.Application.Common.Security;
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
    ICurrentActor actor,
    IRequestContext requestContext,
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

        var idempotencyKey = requestContext.IdempotencyKey;
        products.Add(product);
        products.AddMovement(StockMovement.ForInitialStock(product, actor.Name, idempotencyKey));
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRequestException) when (idempotencyKey is not null)
        {
            // Created before by this same request: answer with that product instead of a second one.
            var original = await productQueries.FindCreatedByRequestAsync(idempotencyKey, CancellationToken.None);
            if (original is not null)
            {
                return original;
            }

            throw;
        }

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

        var stockBefore = product.StockQuantity;
        var now = timeProvider.GetUtcNow();
        product.Update(
            request.Name!,
            request.Description,
            request.Price!.Value,
            request.StockQuantity!.Value,
            categoryId,
            now);

        // Overwriting the stock is recorded too, in the same transaction, so the ledger always adds up.
        if (StockMovement.ForCorrection(product, stockBefore, actor.Name, now) is { } correction)
        {
            products.AddMovement(correction);
        }

        var saved = await SaveCheckingConcurrencyAsync(id, expectedVersion, cancellationToken);
        if (saved.IsFailure)
        {
            return saved.Error;
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

        var saved = await SaveCheckingConcurrencyAsync(id, expectedVersion, cancellationToken);
        if (saved.IsFailure)
        {
            return saved;
        }

        LogProductDeleted(id);
        return Result.Success();
    }

    /// <summary>
    /// Saves a load-modify-save change. If the row changed between our read and our write (the token we loaded no
    /// longer matches), a client that sent a version gets 412, one that did not gets 409.
    /// </summary>
    private async Task<Result> SaveCheckingConcurrencyAsync(
        int id,
        uint? expectedVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (ConcurrencyConflictException)
        {
            LogConcurrencyConflict(id);
            return expectedVersion is null ? ProductErrors.ConcurrentModification(id) : ProductErrors.VersionMismatch(id);
        }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optimistic concurrency conflict on product {ProductId}")]
    private partial void LogConcurrencyConflict(int productId);
}
