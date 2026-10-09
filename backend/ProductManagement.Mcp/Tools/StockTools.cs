using System.ComponentModel;
using ModelContextProtocol.Server;
using ProductManagement.Mcp.Api;

namespace ProductManagement.Mcp.Tools;

/// <summary>Stock movements. The API applies them atomically and never lets stock go below zero.</summary>
[McpServerToolType]
internal sealed class StockTools(ProductApiClient api)
{
    [McpServerTool(Name = "add_stock", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Records a delivery: adds the given quantity to a product's stock and returns the updated product. Calling it twice adds twice.")]
    public Task<Product> AddStock(
        [Description("Six-digit product id.")] int productId,
        [Description("Units to add (1 - 1,000,000).")] int quantity,
        CancellationToken cancellationToken = default) =>
        ToolErrors.CallAsync(() => api.AdjustStockAsync(productId, quantity, add: true, cancellationToken));

    [McpServerTool(Name = "remove_stock", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Records a sale or write-off: removes the given quantity from a product's stock. Fails, without changing anything, if fewer units are available. Calling it twice removes twice.")]
    public Task<Product> RemoveStock(
        [Description("Six-digit product id.")] int productId,
        [Description("Units to remove (1 - 1,000,000).")] int quantity,
        CancellationToken cancellationToken = default) =>
        ToolErrors.CallAsync(() => api.AdjustStockAsync(productId, quantity, add: false, cancellationToken));
}
