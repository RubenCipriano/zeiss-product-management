using ProductManagement.Application.Categories.Contracts;
using ProductManagement.Application.Common.Results;

namespace ProductManagement.Application.Categories;

public interface ICategoryQueryService
{
    Task<Result<IReadOnlyList<CategoryResponse>>> ListAsync(CancellationToken cancellationToken);
}
