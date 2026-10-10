using ProductManagement.Core.Categories.Contracts;

namespace ProductManagement.Core.Categories.Abstractions;

/// <summary>
/// What the category use cases need from the database: does a category exist, and the list of all of them.
/// Implemented in Infrastructure (<c>CategoryQueries</c>).
/// </summary>
public interface ICategoryQueries
{
    Task<bool> ExistsAsync(int id, CancellationToken cancellationToken);

    Task<IReadOnlyList<CategoryResponse>> ListAsync(CancellationToken cancellationToken);
}
