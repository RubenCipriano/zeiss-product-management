namespace ProductManagement.Application.Categories;

internal sealed class CategoryQueryService(ICategoryQueries categories) : ICategoryQueryService
{
    public Task<IReadOnlyList<CategoryResponse>> ListAsync(CancellationToken cancellationToken) =>
        categories.ListAsync(cancellationToken);
}
