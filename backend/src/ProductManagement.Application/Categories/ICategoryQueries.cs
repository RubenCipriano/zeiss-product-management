namespace ProductManagement.Application.Categories;

/// <summary>
/// Read-only access to categories (implemented with no-tracking queries).
/// </summary>
public interface ICategoryQueries
{
    Task<bool> ExistsAsync(int id, CancellationToken cancellationToken);

    Task<IReadOnlyList<CategoryResponse>> ListAsync(CancellationToken cancellationToken);
}
