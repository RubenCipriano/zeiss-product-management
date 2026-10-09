using ProductManagement.Domain.Common;

namespace ProductManagement.Domain.Categories;

/// <summary>
/// Reference data used to classify products. Categories are seeded with the schema and are read-only through the API.
/// </summary>
public sealed class Category
{
    public const int NameMaxLength = 100;

    // Required by EF Core for materialization.
    private Category()
    {
        Name = string.Empty;
    }

    public Category(int id, string name)
    {
        if (id <= 0)
        {
            throw new DomainException("Category id must be positive.");
        }

        Id = id;
        Name = Guard.AgainstInvalidText(name, NameMaxLength, "Category name");
    }

    public int Id { get; private set; }

    public string Name { get; private set; }
}
