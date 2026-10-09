using System.ComponentModel;
using ModelContextProtocol.Server;
using ProductManagement.Mcp.Api;

namespace ProductManagement.Mcp.Tools;

/// <summary>Read-only tools over the product catalogue.</summary>
[McpServerToolType]
internal sealed class ProductTools(ProductApiClient api)
{
    private const int LowStockThreshold = 10;

    [McpServerTool(Name = "search_products", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Finds products whose name contains the given text (case-insensitive). Returns id, name, price, category and current stock for each match.")]
    public Task<ProductPage> SearchProducts(
        [Description("Part of the product name, e.g. 'lens' or 'frame'.")] string name,
        [Description("1-based page number.")] int page = 1,
        [Description("Results per page (1-100).")] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        ToolErrors.CallAsync(() => api.SearchAsync(name, page, pageSize, cancellationToken));

    [McpServerTool(Name = "get_product", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Gets one product by its six-digit id, including the current stock.")]
    public Task<Product> GetProduct(
        [Description("Six-digit product id, e.g. 100004.")] int productId,
        CancellationToken cancellationToken = default) =>
        ToolErrors.CallAsync(() => api.GetAsync(productId, cancellationToken));

    [McpServerTool(Name = "list_products", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists all products ordered by name, one page at a time.")]
    public Task<ProductPage> ListProducts(
        [Description("1-based page number.")] int page = 1,
        [Description("Results per page (1-100).")] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        ToolErrors.CallAsync(() => api.ListAsync(page, pageSize, cancellationToken));

    [McpServerTool(Name = "list_products_by_stock_level", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists products whose stock is within an inclusive range, lowest stock first. Omit a bound to leave it open.")]
    public Task<ProductPage> ListProductsByStockLevel(
        [Description("Minimum stock (inclusive), or null for no lower bound.")] int? min = null,
        [Description("Maximum stock (inclusive), or null for no upper bound.")] int? max = null,
        [Description("1-based page number.")] int page = 1,
        [Description("Results per page (1-100).")] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        ToolErrors.CallAsync(() => api.ByStockLevelAsync(min, max, page, pageSize, cancellationToken));

    [McpServerTool(Name = "list_low_stock", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists products that are running low (stock at or below 10 units, including out of stock), lowest first. Useful for restocking decisions.")]
    public Task<ProductPage> ListLowStock(CancellationToken cancellationToken = default) =>
        ToolErrors.CallAsync(() => api.ByStockLevelAsync(0, LowStockThreshold, 1, 100, cancellationToken));

    [McpServerTool(Name = "list_categories", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the product categories.")]
    public Task<IReadOnlyList<Category>> ListCategories(CancellationToken cancellationToken = default) =>
        ToolErrors.CallAsync(() => api.CategoriesAsync(cancellationToken));
}
