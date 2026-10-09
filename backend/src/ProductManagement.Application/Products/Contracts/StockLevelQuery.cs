using ProductManagement.Application.Common.Paging;

namespace ProductManagement.Application.Products.Contracts;

public sealed record StockLevelQuery : PageQuery
{
    /// <summary>Inclusive lower bound of the stock quantity. Omit for no lower bound.</summary>
    public int? Min { get; init; }

    /// <summary>Inclusive upper bound of the stock quantity. Omit for no upper bound.</summary>
    public int? Max { get; init; }
}
