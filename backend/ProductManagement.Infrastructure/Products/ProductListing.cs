namespace ProductManagement.Infrastructure.Products;

/// <summary>
/// Which products a paged list shows, and therefore how it is filtered and ordered. A closed set of options: the
/// private constructor means only the three nested records below can exist.
/// </summary>
internal abstract record ProductListing
{
    private ProductListing()
    {
    }

    /// <summary>Every product, by name.</summary>
    public record All : ProductListing;

    /// <summary>Products whose name contains <paramref name="Fragment"/> (case-insensitive), by name.</summary>
    public record NameContains(string Fragment) : ProductListing;

    /// <summary>
    /// Products whose stock lies within the inclusive bounds (open when <c>null</c>), lowest stock first.
    /// </summary>
    public record StockBetween(int? Min, int? Max) : ProductListing;
}
