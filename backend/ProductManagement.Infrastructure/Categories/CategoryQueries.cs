using Microsoft.EntityFrameworkCore;
using ProductManagement.Core.Categories.Abstractions;
using ProductManagement.Core.Categories.Contracts;
using ProductManagement.Infrastructure.Persistence;

namespace ProductManagement.Infrastructure.Categories;

/// <summary>
/// Reads categories from PostgreSQL for the category use cases (EF Core queries; <c>AsNoTracking</c> = read-only,
/// EF Core keeps no copy to watch for changes).
/// </summary>
internal class CategoryQueries(ProductManagementDbContext context) : ICategoryQueries
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
