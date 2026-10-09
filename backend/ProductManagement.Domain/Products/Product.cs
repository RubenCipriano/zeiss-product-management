using ProductManagement.Domain.Categories;
using ProductManagement.Domain.Common;

namespace ProductManagement.Domain.Products;

/// <summary>
/// A product that can be sold and whose stock is tracked.
/// </summary>
/// <remarks>
/// State only changes through intention-revealing methods, so an instance can never hold invalid data.
/// </remarks>
public sealed class Product
{
    // Required by EF Core for materialization.
    private Product()
    {
        Name = string.Empty;
    }

    public int Id { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public decimal Price { get; private set; }

    public int StockQuantity { get; private set; }

    public int CategoryId { get; private set; }

    public Category? Category { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Optimistic concurrency token. The database changes it on every write, so it must never be set by the domain.
    /// </summary>
    public uint Version { get; private set; }

    public static Product Create(
        int id,
        string name,
        string? description,
        decimal price,
        int stockQuantity,
        int categoryId,
        DateTimeOffset now)
    {
        if (!ProductRules.IsValidId(id))
        {
            throw new DomainException(
                $"Product id must be a six-digit number between {ProductRules.MinId} and {ProductRules.MaxId}.");
        }

        var product = new Product { Id = id, CreatedAt = now };
        product.Apply(name, description, price, stockQuantity, categoryId, now);
        return product;
    }

    /// <summary>
    /// Replaces the editable details of the product (PUT semantics).
    /// </summary>
    public void Update(
        string name,
        string? description,
        decimal price,
        int stockQuantity,
        int categoryId,
        DateTimeOffset now) =>
        Apply(name, description, price, stockQuantity, categoryId, now);

    private void Apply(
        string name,
        string? description,
        decimal price,
        int stockQuantity,
        int categoryId,
        DateTimeOffset now)
    {
        // Validate everything before assigning anything, so a failed update never leaves the entity half-modified.
        var validName = Guard.AgainstInvalidText(name, ProductRules.NameMaxLength, nameof(Name));
        var validDescription = Guard.AgainstTooLongOptionalText(
            description, ProductRules.DescriptionMaxLength, nameof(Description));

        Guard.AgainstOutOfRange(price, ProductRules.MinPrice, ProductRules.MaxPrice, nameof(Price));
        if (!ProductRules.HasValidPricePrecision(price))
        {
            throw new DomainException(
                $"{nameof(Price)} must not have more than {ProductRules.PriceDecimalPlaces} decimal places.");
        }

        Guard.AgainstOutOfRange(stockQuantity, 0, ProductRules.MaxStockQuantity, nameof(StockQuantity));

        if (categoryId <= 0)
        {
            throw new DomainException($"{nameof(CategoryId)} must be positive.");
        }

        Name = validName;
        Description = validDescription;
        Price = price;
        StockQuantity = stockQuantity;
        CategoryId = categoryId;
        UpdatedAt = now;
    }
}
