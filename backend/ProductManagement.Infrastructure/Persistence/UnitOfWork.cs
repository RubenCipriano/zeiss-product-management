using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProductManagement.Application.Common.Persistence;
using ProductManagement.Domain.Products;
using ProductManagement.Infrastructure.Caching;
using ProductManagement.Infrastructure.Products;

namespace ProductManagement.Infrastructure.Persistence;

internal sealed class UnitOfWork(ProductManagementDbContext context, ICatalogCache cache) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        var changedProducts = context.ChangeTracker.Entries<Product>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToList();

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
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres && ProductRepository.IsDuplicateRequest(postgres))
        {
            throw new DuplicateRequestException("This request was already applied.", exception);
        }

        // Only after the commit: invalidating earlier would let a concurrent reader re-cache the old state.
        if (changedProducts.Count > 0)
        {
            await cache.InvalidateAsync(CatalogChange.Products, changedProducts);
        }
    }
}
