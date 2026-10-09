using Microsoft.Extensions.Logging;
using ProductManagement.Application.Common.Requests;
using ProductManagement.Application.Common.Results;
using ProductManagement.Application.Common.Security;
using ProductManagement.Application.Products.Abstractions;
using ProductManagement.Application.Products.Contracts;
using ProductManagement.Domain.Products;

namespace ProductManagement.Application.Products;

/// <summary>
/// Stock changes skip load-modify-save on purpose: a single conditional UPDATE makes check-and-write atomic.
/// Concurrent requests therefore never oversell and never fail with spurious concurrency conflicts.
/// See docs/adr/0003-atomic-stock-updates.md.
/// </summary>
internal sealed partial class StockCommandService(
    IProductRepository products,
    IProductQueries productQueries,
    ICurrentActor actor,
    IRequestContext requestContext,
    TimeProvider timeProvider,
    ILogger<StockCommandService> logger) : IStockCommandService
{
    public Task<Result<ProductResponse>> DecrementStockAsync(int id, int quantity, CancellationToken cancellationToken) =>
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

        var delta = operation == StockOperation.Remove ? -quantity : quantity;
        var adjustment = StockAdjustment.Plan(delta, actor.Name, requestContext.IdempotencyKey, timeProvider.GetUtcNow());
        var outcome = adjustment is null
            ? StockAdjustmentOutcome.NotApplied
            : await products.TryAdjustStockAsync(id, adjustment, cancellationToken);
        // Applied before by this same request (its first answer was lost): the change stands, report success.
        var adjusted = outcome is StockAdjustmentOutcome.Applied or StockAdjustmentOutcome.AlreadyApplied;

        // The current state tells us both what to return on success and why it failed otherwise. Not cancellable:
        // once the change may have been committed, the request finishes, so its outcome can be recorded and replayed.
        var current = await productQueries.GetByIdAsync(id, CancellationToken.None);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Adjusted stock of product {ProductId} by {Delta}; now {StockQuantity}")]
    private partial void LogStockAdjusted(int productId, int delta, int stockQuantity);

    private enum StockOperation
    {
        Remove,
        Add,
    }
}
