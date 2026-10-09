namespace ProductManagement.Mcp.Api;

// The MCP server is a client of the HTTP contract, so it has its own copies of the response shapes instead of
// referencing the API's assemblies (it could live in a different repository).

internal sealed record Product(
    int Id,
    string Name,
    string? Description,
    decimal Price,
    int StockQuantity,
    int CategoryId,
    string CategoryName,
    DateTimeOffset UpdatedAt,
    uint Version);

internal sealed record ProductPage(IReadOnlyList<Product> Items, int Page, int PageSize, int TotalCount, int TotalPages);

internal sealed record Category(int Id, string Name);

internal sealed record ApiProblem(int Status, string? Title, string? Detail, string? ErrorCode);
