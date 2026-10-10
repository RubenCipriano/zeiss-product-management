namespace ProductManagement.Core.Products.Contracts;

/// <summary>
/// The JSON body of <c>PUT /api/products/{id}</c>: replaces every editable field, so every required field must be
/// sent.
/// </summary>
public record UpdateProductRequest : IProductWriteRequest
{
    /// <summary>Product name (required, max 200 characters).</summary>
    public string? Name { get; init; }

    /// <summary>Optional description (max 2000 characters). Omitting it clears the description.</summary>
    public string? Description { get; init; }

    /// <summary>Unit price (0.01 - 1,000,000, at most two decimal places).</summary>
    public decimal? Price { get; init; }

    /// <summary>Stock (0 - 1,000,000).</summary>
    public int? StockQuantity { get; init; }

    /// <summary>Identifier of an existing category.</summary>
    public int? CategoryId { get; init; }
}
