using ProductManagement.Application.Categories.Abstractions;
using ProductManagement.Application.Categories.Contracts;
using ProductManagement.Application.Common.Results;

namespace ProductManagement.Application.Categories;

internal sealed class CategoryQueryService(ICategoryQueries categories) : ICategoryQueryService
{
    public async Task<Result<IReadOnlyList<CategoryResponse>>> ListAsync(CancellationToken cancellationToken) =>
        Result<IReadOnlyList<CategoryResponse>>.Success(await categories.ListAsync(cancellationToken));
}
