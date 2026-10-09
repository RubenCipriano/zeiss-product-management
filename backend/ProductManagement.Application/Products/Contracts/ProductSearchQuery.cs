using ProductManagement.Application.Common.Paging;

namespace ProductManagement.Application.Products.Contracts;

public sealed record ProductSearchQuery : PageQuery
{
    /// <summary>Case-insensitive fragment of the product name (partial match).</summary>
    public string? Name { get; init; }
}
