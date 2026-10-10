using FluentValidation;
using Microsoft.Extensions.Logging;
using ProductManagement.Core.Categories.Abstractions;
using ProductManagement.Core.Common.Persistence;
using ProductManagement.Core.Common.Requests;
using ProductManagement.Core.Common.Results;
using ProductManagement.Core.Common.Security;
using ProductManagement.Core.Common.Validation;
using ProductManagement.Core.Products.Abstractions;
using ProductManagement.Core.Products.Contracts;

namespace ProductManagement.Core.Products;

/// <summary>
/// Handles create, edit (PUT) and delete product requests: validates the input, checks the category exists, saves the
/// change (with its stock ledger entry, in the same transaction) and returns the saved product.
/// </summary>
/// <remarks>
/// Edits and deletes are protected against lost updates: if the product changed after the client read it
/// (<c>If-Match</c>) or after we loaded it, the change is refused instead of silently overwriting the newer data.
/// See ADR 0004 (optimistic concurrency).
/// </remarks>
internal partial class ProductCommandService(
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

        var id = await NextIdAsync(cancellationToken);
        if (id.IsFailure)
        {
            return id.Error;
        }

        var product = Product.Create(
            id.Value,
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
            return await AnswerRepeatedCreateAsync(idempotencyKey);
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

        var loaded = await LoadForChangeAsync(id, expectedVersion, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var product = loaded.Value;
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
        var loaded = await LoadForChangeAsync(id, expectedVersion, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        products.Remove(loaded.Value);

        var saved = await SaveCheckingConcurrencyAsync(id, expectedVersion, cancellationToken);
        if (saved.IsFailure)
        {
            return saved;
        }

        LogProductDeleted(id);
        return Result.Success();
    }

    /// <summary>A new product id, or a 503 error when every six-digit id has been used.</summary>
    private async Task<Result<int>> NextIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await idGenerator.NextIdAsync(cancellationToken);
        }
        catch (IdentifiersExhaustedException exception)
        {
            LogProductIdsExhausted(exception);
            return ProductErrors.IdsExhausted();
        }
    }

    /// <summary>
    /// The database refused the create because its idempotency key is already recorded. If this same request created
    /// a product before (its first answer was lost), answer with that product instead of creating a second one.
    /// Otherwise the key belongs to a stock change: it was reused for this create after the idempotency store forgot
    /// it.
    /// </summary>
    private async Task<Result<ProductResponse>> AnswerRepeatedCreateAsync(string idempotencyKey)
    {
        var original = await productQueries.FindCreatedByRequestAsync(idempotencyKey, CancellationToken.None);
        return original is not null ? original : RequestErrors.IdempotencyKeyReused();
    }

    /// <summary>
    /// Loads the product to edit or delete: 404 if it does not exist, 412 if the client sent a version
    /// (<c>If-Match</c>) and the product has changed since.
    /// </summary>
    private async Task<Result<Product>> LoadForChangeAsync(
        int id,
        uint? expectedVersion,
        CancellationToken cancellationToken)
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

        return product;
    }

    /// <summary>
    /// Saves a load-change-save edit. If the row changed between our read and our write (someone else saved in
    /// between), a client that sent a version gets 412, one that did not gets 409.
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
            return expectedVersion is null
                ? ProductErrors.ConcurrentModification(id)
                : ProductErrors.VersionMismatch(id);
        }
    }

    private async Task<Result<ProductResponse>> LoadAsync(int id, CancellationToken cancellationToken)
    {
        // Re-read through the query side so every endpoint returns the exact same representation
        // (including the category name and the database-generated version).
        var response = await productQueries.GetByIdAsync(id, cancellationToken);
        return response is null ? ProductErrors.NotFound(id) : response;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Created product {ProductId} with {StockQuantity} unit(s) in stock")]
    private partial void LogProductCreated(int productId, int stockQuantity);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated product {ProductId}")]
    private partial void LogProductUpdated(int productId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted product {ProductId}")]
    private partial void LogProductDeleted(int productId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optimistic concurrency conflict on product {ProductId}")]
    private partial void LogConcurrencyConflict(int productId);

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "No product identifiers left: products can no longer be created")]
    private partial void LogProductIdsExhausted(Exception exception);
}
