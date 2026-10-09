namespace ProductManagement.Application.Products.Contracts;

/// <summary>
/// Representation of a product returned by every product endpoint.
/// </summary>
/// <param name="Id">Unique six-digit identifier.</param>
/// <param name="StockQuantity">Units currently available in stock.</param>
/// <param name="Version">
/// Opaque concurrency token. Send it back in an <c>If-Match</c> header on PUT/DELETE so a stale update is rejected
/// instead of overwriting someone else's changes. It is also returned as the <c>ETag</c> response header.
/// </param>
public sealed record ProductResponse(
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
