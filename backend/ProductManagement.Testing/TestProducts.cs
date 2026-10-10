using ProductManagement.Core.Products.Contracts;

namespace ProductManagement.Testing;

/// <summary>
/// Builders for valid requests. Tests override only the fields that matter to them, so each test reads as a
/// statement of what is special about its data.
/// </summary>
public static class TestProducts
{
    public const int LensesCategoryId = 1;

    public const int FramesCategoryId = 2;

    public static CreateProductRequest CreateRequest(
        string name = "Test Product",
        int stockQuantity = 10,
        decimal price = 99.90m,
        int categoryId = LensesCategoryId,
        string? description = "A product created by an automated test.") =>
        new()
        {
            Name = name,
            Description = description,
            Price = price,
            StockQuantity = stockQuantity,
            CategoryId = categoryId,
        };

    public static UpdateProductRequest UpdateRequest(
        ProductResponse current,
        string? name = null,
        int? stockQuantity = null,
        decimal? price = null,
        int? categoryId = null) =>
        new()
        {
            Name = name ?? current.Name,
            Description = current.Description,
            Price = price ?? current.Price,
            StockQuantity = stockQuantity ?? current.StockQuantity,
            CategoryId = categoryId ?? current.CategoryId,
        };
}
