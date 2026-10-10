using ProductManagement.Core.Common.Paging;

namespace ProductManagement.Core.Products.Contracts;

/// <summary>The query string of <c>GET /api/products/stock-level</c>: a stock range plus paging.</summary>
public record StockLevelQuery : PageQuery
{
    /// <summary>Inclusive lower bound of the stock quantity. Omit for no lower bound.</summary>
    public int? Min { get; init; }

    /// <summary>Inclusive upper bound of the stock quantity. Omit for no upper bound.</summary>
    public int? Max { get; init; }
}
