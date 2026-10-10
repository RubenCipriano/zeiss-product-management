namespace ProductManagement.Core.Common.Persistence;

/// <summary>
/// Saves every change made during the current request in one database transaction: all of it or none of it.
/// </summary>
public interface IUnitOfWork
{
    /// <exception cref="ConcurrencyConflictException">
    /// The data was modified by someone else since it was read.
    /// </exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
