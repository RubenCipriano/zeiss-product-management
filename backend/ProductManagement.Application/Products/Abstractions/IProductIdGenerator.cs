namespace ProductManagement.Application.Products.Abstractions;

/// <summary>
/// Produces unique six-digit product identifiers.
/// </summary>
/// <remarks>
/// Implementations must be safe across processes: several API instances may call this at the same time and must
/// never receive the same value. See docs/adr/0002-product-id-generation.md.
/// </remarks>
public interface IProductIdGenerator
{
    Task<int> NextIdAsync(CancellationToken cancellationToken);
}
