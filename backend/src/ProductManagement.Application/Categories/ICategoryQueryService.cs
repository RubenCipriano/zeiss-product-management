namespace ProductManagement.Application.Categories;

public interface ICategoryQueryService
{
    Task<IReadOnlyList<CategoryResponse>> ListAsync(CancellationToken cancellationToken);
}
