using Microsoft.EntityFrameworkCore;
using ProductManagement.Application.Common.Persistence;
using ProductManagement.Domain.Products;
using ProductManagement.Infrastructure.Caching;

namespace ProductManagement.Infrastructure.Persistence;

internal sealed class UnitOfWork(ProductManagementDbContext context, ICatalogCache cache) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        var catalogueChanges = context.ChangeTracker.Entries<Product>()
            .Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // Keep the application layer free of EF Core types.
            throw new ConcurrencyConflictException(
                "The entity was modified or deleted by another operation after it was loaded.",
                exception);
        }

        // Only after the commit: invalidating earlier would let a concurrent reader re-cache the old state.
        if (catalogueChanges)
        {
            await cache.InvalidateAsync(CatalogChange.Products);
        }
    }
}
