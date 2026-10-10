namespace ProductManagement.Core.Products.Abstractions;

/// <summary>
/// Hands out a new, unique six-digit product id. Implemented in Infrastructure with a PostgreSQL sequence.
/// </summary>
/// <remarks>
/// Implementations must be safe across processes: several API instances may call this at the same time and must
/// never receive the same value. See ADR 0002 (product id generation).
/// </remarks>
public interface IProductIdGenerator
{
    /// <exception cref="Common.Persistence.IdentifiersExhaustedException">
    /// Every six-digit identifier has been assigned.
    /// </exception>
    Task<int> NextIdAsync(CancellationToken cancellationToken);
}
