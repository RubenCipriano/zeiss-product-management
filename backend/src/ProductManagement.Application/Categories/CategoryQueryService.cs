using ProductManagement.Application.Categories.Abstractions;
using ProductManagement.Application.Categories.Contracts;

namespace ProductManagement.Application.Categories;

internal sealed class CategoryQueryService(ICategoryQueries categories) : ICategoryQueryService
{
    public Task<IReadOnlyList<CategoryResponse>> ListAsync(CancellationToken cancellationToken) =>
        categories.ListAsync(cancellationToken);
}
