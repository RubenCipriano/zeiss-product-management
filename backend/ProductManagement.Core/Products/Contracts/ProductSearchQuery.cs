using ProductManagement.Core.Common.Paging;

namespace ProductManagement.Core.Products.Contracts;

/// <summary>The query string of <c>GET /api/products/search</c>: a name fragment plus paging.</summary>
public record ProductSearchQuery : PageQuery
{
    /// <summary>Case-insensitive fragment of the product name (partial match).</summary>
    public string? Name { get; init; }
}
