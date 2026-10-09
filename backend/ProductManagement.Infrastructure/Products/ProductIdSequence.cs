using Microsoft.EntityFrameworkCore;
using ProductManagement.Domain.Products;

namespace ProductManagement.Infrastructure.Products;

/// <summary>
/// The PostgreSQL sequence that hands out product identifiers.
/// </summary>
/// <remarks>
/// <c>nextval()</c> is atomic and non-transactional: concurrent callers, from any number of API instances,
/// never receive the same value, even if their transactions roll back. The range is limited to six-digit
/// numbers and <c>NO CYCLE</c> guarantees an identifier is never reused. See docs/adr/0002.
/// </remarks>
internal static class ProductIdSequence
{
    public const string Name = "product_id_seq";

    public static void Configure(ModelBuilder modelBuilder) =>
        modelBuilder.HasSequence<int>(Name)
            .StartsAt(ProductRules.MinId)
            .IncrementsBy(1)
            .HasMin(ProductRules.MinId)
            .HasMax(ProductRules.MaxId)
            .IsCyclic(false);
}
