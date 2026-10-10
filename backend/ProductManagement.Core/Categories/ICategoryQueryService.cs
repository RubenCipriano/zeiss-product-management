using ProductManagement.Core.Categories.Contracts;
using ProductManagement.Core.Common.Results;

namespace ProductManagement.Core.Categories;

/// <summary>The category operation the API calls: list every category, ordered by name.</summary>
public interface ICategoryQueryService
{
    Task<Result<IReadOnlyList<CategoryResponse>>> ListAsync(CancellationToken cancellationToken);
}
