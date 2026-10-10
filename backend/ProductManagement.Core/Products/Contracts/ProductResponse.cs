namespace ProductManagement.Core.Products.Contracts;

/// <summary>
/// A product as the API returns it. Every product endpoint uses this same shape.
/// </summary>
/// <param name="Id">Unique six-digit identifier.</param>
/// <param name="StockQuantity">Units currently available in stock.</param>
/// <param name="Version">
/// The product's version; it changes on every save. Also returned as the <c>ETag</c> header. Send it back in an
/// <c>If-Match</c> header on PUT/DELETE so that, if someone else changed the product in the meantime, your request is
/// refused instead of overwriting their change.
/// </param>
public record ProductResponse(
    int Id,
    string Name,
    string? Description,
    decimal Price,
    int StockQuantity,
    int CategoryId,
    string CategoryName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);
