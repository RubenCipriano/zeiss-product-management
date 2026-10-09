using System.Linq.Expressions;
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

    /// <summary>
    /// The stock rule as a predicate: after adding <paramref name="delta"/> (which may be negative), the stock
    /// must stay between zero and <see cref="ProductRules.MaxStockQuantity"/>.
    /// </summary>
    /// <remarks>
    /// The rule is an expression tree rather than a method so that persistence can evaluate it inside a single
    /// conditional <c>UPDATE</c>. Check and write then happen atomically in the database, which is what keeps
    /// stock correct when many requests change the same product at once. See docs/adr/0003.
    /// </remarks>
    public static Expression<Func<Product, bool>> CanAdjustStockBy(int delta)
    {
        if (StockRangeAllowingAdjustment(delta) is not var (min, max))
        {
            return _ => false;
        }

        return product => product.StockQuantity >= min && product.StockQuantity <= max;
    }

    /// <summary>
    /// The stock range in which adding <paramref name="delta"/> keeps the stock between zero and
    /// <see cref="ProductRules.MaxStockQuantity"/>, or <c>null</c> when no stock allows it. Used by persistence to put
    /// the rule into a single atomic SQL statement.
    /// </summary>
    public static (int Min, int Max)? StockRangeAllowingAdjustment(int delta)
    {
        // Computed here, in C#, so the SQL never evaluates "stock + delta" and cannot overflow.
        var minimumCurrentStock = delta < 0 ? -(long)delta : 0L;
        var maximumCurrentStock = (long)ProductRules.MaxStockQuantity - Math.Max(delta, 0);

        return minimumCurrentStock > ProductRules.MaxStockQuantity || maximumCurrentStock < 0
            ? null
            : ((int)minimumCurrentStock, (int)maximumCurrentStock);
    }

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
