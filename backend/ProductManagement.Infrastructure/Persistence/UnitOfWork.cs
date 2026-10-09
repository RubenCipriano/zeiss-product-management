using Microsoft.EntityFrameworkCore;
using Npgsql;
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
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres && PostgresErrors.IsDuplicateRequestKey(postgres))
        {
            throw new DuplicateRequestException("This request was already applied.", exception);
        }
    }
}
