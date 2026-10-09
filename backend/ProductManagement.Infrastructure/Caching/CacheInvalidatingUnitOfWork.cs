using Microsoft.EntityFrameworkCore;
using ProductManagement.Application.Common.Persistence;
using ProductManagement.Domain.Products;
using ProductManagement.Infrastructure.Persistence;

namespace ProductManagement.Infrastructure.Caching;

/// <summary>
/// Invalidates the read cache for the products a unit of work created, edited or deleted, once it has committed.
/// </summary>
internal sealed class CacheInvalidatingUnitOfWork(
    IUnitOfWork inner,
    ProductManagementDbContext context,
    ICatalogCache cache) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        // Read before saving: afterwards every entry is Unchanged (or detached, for a delete).
        var changedProducts = context.ChangeTracker.Entries<Product>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToList();

        await inner.SaveChangesAsync(cancellationToken);

        // Only after the commit: invalidating earlier would let a concurrent reader re-cache the old state.
        if (changedProducts.Count > 0)
        {
            await cache.InvalidateAsync(CatalogChange.Products, changedProducts);
        }
    }
}
