using ProductManagement.Core.Categories;
using ProductManagement.Core.Common;

namespace ProductManagement.Core.Products;

/// <summary>
/// A product in the catalogue: name, description, price, stock and category.
/// </summary>
/// <remarks>
/// Its data only changes through <see cref="Create"/> and <see cref="Update"/>, which validate everything first, so a
/// <see cref="Product"/> object never holds invalid data. Stock additions and removals do not go through this class:
/// they are applied directly in the database (see <see cref="StockAdjustment"/>).
/// </remarks>
public class Product
{
    // Used by EF Core (the database library) to rebuild the object from a table row.
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
    /// The row's version, sent to clients as the <c>ETag</c>. PostgreSQL changes it on every write (it is the hidden
    /// <c>xmin</c> column), so the code never sets it.
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
