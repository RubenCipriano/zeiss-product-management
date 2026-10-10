namespace ProductManagement.Core.Products.Contracts;

/// <summary>The JSON body of <c>POST /api/products</c>.</summary>
public record CreateProductRequest : IProductWriteRequest
{
    /// <summary>Product name (required, max 200 characters).</summary>
    public string? Name { get; init; }

    /// <summary>Optional description (max 2000 characters).</summary>
    public string? Description { get; init; }

    /// <summary>Unit price (0.01 - 1,000,000, at most two decimal places).</summary>
    public decimal? Price { get; init; }

    /// <summary>Initial stock (0 - 1,000,000).</summary>
    public int? StockQuantity { get; init; }

    /// <summary>Identifier of an existing category.</summary>
    public int? CategoryId { get; init; }
}
