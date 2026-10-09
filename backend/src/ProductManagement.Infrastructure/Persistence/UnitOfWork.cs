using Microsoft.EntityFrameworkCore;
using ProductManagement.Application.Common.Persistence;

namespace ProductManagement.Infrastructure.Persistence;

internal sealed class UnitOfWork(ProductManagementDbContext context) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
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
    }
}
