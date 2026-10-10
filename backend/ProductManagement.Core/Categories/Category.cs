using ProductManagement.Core.Common;

namespace ProductManagement.Core.Categories;

/// <summary>
/// A product category (e.g. "Frames"). Fixed reference data: created by a database migration and read-only through
/// the API.
/// </summary>
public class Category
{
    public const int NameMaxLength = 100;

    // Used by EF Core (the database library) to rebuild the object from a table row.
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
