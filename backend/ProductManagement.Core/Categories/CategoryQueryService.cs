using ProductManagement.Core.Categories.Abstractions;
using ProductManagement.Core.Categories.Contracts;
using ProductManagement.Core.Common.Results;

namespace ProductManagement.Core.Categories;

/// <summary>Handles "list categories" requests (GET /api/categories): reads them from the database.</summary>
internal class CategoryQueryService(ICategoryQueries categories) : ICategoryQueryService
{
    public async Task<Result<IReadOnlyList<CategoryResponse>>> ListAsync(CancellationToken cancellationToken) =>
        Result<IReadOnlyList<CategoryResponse>>.Success(await categories.ListAsync(cancellationToken));
}
