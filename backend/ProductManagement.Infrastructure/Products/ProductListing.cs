namespace ProductManagement.Infrastructure.Products;

/// <summary>Which products a paged list shows, and therefore how it is filtered and ordered.</summary>
internal abstract record ProductListing
{
    private ProductListing()
    {
    }

    /// <summary>Every product, by name.</summary>
    public sealed record All : ProductListing;

    /// <summary>Products whose name contains <paramref name="Fragment"/> (case-insensitive), by name.</summary>
    public sealed record NameContains(string Fragment) : ProductListing;

    /// <summary>Products whose stock lies within the inclusive bounds (open when <c>null</c>), lowest stock first.</summary>
    public sealed record StockBetween(int? Min, int? Max) : ProductListing;
}
