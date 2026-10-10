using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ProductManagement.Core.Common.Requests;
using ProductManagement.Core.Common.Results;
using ProductManagement.Core.Common.Security;
using ProductManagement.Core.Products.Abstractions;
using ProductManagement.Core.Products.Contracts;

namespace ProductManagement.Core.Products;

/// <summary>
/// Handles "remove from stock" and "add to stock" requests: checks the quantity, asks the database to apply the change
/// atomically (check and update in one statement), and returns the product with its new stock.
/// </summary>
/// <remarks>
/// It deliberately does not load the product, change it and save it back: with many simultaneous requests that would
/// oversell or fail with needless conflicts. See ADR 0003 (atomic stock updates).
/// </remarks>
internal partial class StockCommandService(
    IProductRepository products,
    IProductQueries productQueries,
    ICurrentActor actor,
    IRequestContext requestContext,
    TimeProvider timeProvider,
    ILogger<StockCommandService> logger) : IStockCommandService
{
    public Task<Result<ProductResponse>> DecrementStockAsync(
        int id,
        int quantity,
        CancellationToken cancellationToken) =>
        AdjustStockAsync(id, quantity, StockOperation.Remove, cancellationToken);

    public Task<Result<ProductResponse>> AddToStockAsync(int id, int quantity, CancellationToken cancellationToken) =>
        AdjustStockAsync(id, quantity, StockOperation.Add, cancellationToken);

    private async Task<Result<ProductResponse>> AdjustStockAsync(
        int id,
        int quantity,
        StockOperation operation,
        CancellationToken cancellationToken)
    {
        if (quantity is < ProductRules.MinAdjustmentQuantity or > ProductRules.MaxStockQuantity)
        {
            return ProductErrors.InvalidQuantity();
        }

        // Within the quantity limits checked above, some stock always allows the change, so there is always a plan.
        var delta = operation == StockOperation.Remove ? -quantity : quantity;
        var adjustment = StockAdjustment.Plan(
                delta,
                actor.Name,
                requestContext.IdempotencyKey,
                timeProvider.GetUtcNow())
            ?? throw new UnreachableException($"No stock allows a change of {delta}.");
        var outcome = await products.TryAdjustStockAsync(id, adjustment, cancellationToken);
        if (outcome == StockAdjustmentOutcome.RequestKeyReused)
        {
            return RequestErrors.IdempotencyKeyReused();
        }

        // Applied before by this same request (its first answer was lost): the change stands, report success.
        var stockChanged = outcome is StockAdjustmentOutcome.Applied or StockAdjustmentOutcome.AlreadyApplied;

        // The current state tells us both what to return on success and why it failed otherwise. Not cancellable:
        // once the change may have been committed, the request finishes, so its outcome can be recorded and replayed.
        var current = await productQueries.GetByIdAsync(id, CancellationToken.None);
        if (current is null)
        {
            return ProductErrors.NotFound(id);
        }

        if (!stockChanged)
        {
            return operation == StockOperation.Remove
                ? ProductErrors.InsufficientStock(id, quantity, current.StockQuantity)
                : ProductErrors.StockLimitExceeded(id, quantity, current.StockQuantity);
        }

        LogStockAdjusted(id, delta, current.StockQuantity);
        return current;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Adjusted stock of product {ProductId} by {Delta}; now {StockQuantity}")]
    private partial void LogStockAdjusted(int productId, int delta, int stockQuantity);

    private enum StockOperation
    {
        Remove,
        Add,
    }
}
