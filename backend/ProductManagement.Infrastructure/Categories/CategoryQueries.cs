using Microsoft.EntityFrameworkCore;
using ProductManagement.Application.Categories.Abstractions;
using ProductManagement.Application.Categories.Contracts;
using ProductManagement.Infrastructure.Persistence;

namespace ProductManagement.Infrastructure.Categories;

internal sealed class CategoryQueries(ProductManagementDbContext context) : ICategoryQueries
{
    public Task<bool> ExistsAsync(int id, CancellationToken cancellationToken) =>
        context.Categories.AnyAsync(category => category.Id == id, cancellationToken);

    public async Task<IReadOnlyList<CategoryResponse>> ListAsync(CancellationToken cancellationToken) =>
        await context.Categories
            .AsNoTracking()
            .OrderBy(category => category.Name)
            .Select(category => new CategoryResponse(category.Id, category.Name))
            .ToListAsync(cancellationToken);
}
