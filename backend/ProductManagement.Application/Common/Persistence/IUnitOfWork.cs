namespace ProductManagement.Application.Common.Persistence;

/// <summary>
/// Commits every change tracked during the current request in a single transaction.
/// </summary>
public interface IUnitOfWork
{
    /// <exception cref="ConcurrencyConflictException">The data was modified by someone else since it was read.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
