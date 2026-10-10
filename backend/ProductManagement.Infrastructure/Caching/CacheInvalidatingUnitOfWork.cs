using Microsoft.EntityFrameworkCore;
using ProductManagement.Core.Common.Persistence;
using ProductManagement.Core.Products;
using ProductManagement.Infrastructure.Persistence;

namespace ProductManagement.Infrastructure.Caching;

/// <summary>
/// Wraps the real unit of work (a decorator): after a save has committed, it tells the cache which products were
/// created, edited or deleted, so their old cached copies are no longer served.
/// </summary>
internal class CacheInvalidatingUnitOfWork(
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
