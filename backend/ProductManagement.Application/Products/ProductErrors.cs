using ProductManagement.Application.Common.Errors;
using ProductManagement.Domain.Products;

namespace ProductManagement.Application.Products;

/// <summary>
/// Every expected failure of the product use cases. Codes are part of the public API contract.
/// </summary>
public static class ProductErrors
{
    public static Error NotFound(int id) =>
        Error.NotFound("product.not_found", $"Product {id} was not found.");

    public static Error CategoryNotFound(int categoryId) =>
        Error.Validation("categoryId", $"Category {categoryId} does not exist.");

    public static Error InvalidQuantity() =>
        Error.Validation("quantity", $"'Quantity' must be between 1 and {ProductRules.MaxStockQuantity}.");

    public static Error InsufficientStock(int id, int requested, int available) =>
        Error.Conflict(
            "product.insufficient_stock",
            $"Cannot remove {requested} unit(s) from product {id}: only {available} available.",
            new Dictionary<string, object?> { ["requested"] = requested, ["available"] = available });

    public static Error StockLimitExceeded(int id, int requested, int available) =>
        Error.Conflict(
            "product.stock_limit_exceeded",
            $"Cannot add {requested} unit(s) to product {id}: stock would exceed the maximum of {ProductRules.MaxStockQuantity}.",
            new Dictionary<string, object?>
            {
                ["requested"] = requested,
                ["available"] = available,
                ["maximum"] = ProductRules.MaxStockQuantity,
            });

    public static Error VersionMismatch(int id) =>
        Error.PreconditionFailed(
            "product.version_mismatch",
            $"Product {id} has been modified since it was retrieved. Reload it and try again.");

    public static Error ConcurrentModification(int id) =>
        Error.Conflict(
            "product.concurrent_modification",
            $"Product {id} was modified by another request while this one was being processed. Reload it and try again.");
}
