using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProductManagement.Core.Common.Persistence;

namespace ProductManagement.Infrastructure.Persistence;

/// <summary>
/// Saves the request's changes in one transaction (EF Core <c>SaveChanges</c>) and translates database-specific errors
/// into Core's own exceptions, so Core never references EF Core or the PostgreSQL driver.
/// </summary>
internal class UnitOfWork(ProductManagementDbContext context) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyConflictException(
                "The entity was modified or deleted by another operation after it was loaded.",
                exception);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException postgres
                && PostgresErrors.IsDuplicateRequestKey(postgres))
        {
            throw new DuplicateRequestException("This request was already applied.", exception);
        }
    }
}
