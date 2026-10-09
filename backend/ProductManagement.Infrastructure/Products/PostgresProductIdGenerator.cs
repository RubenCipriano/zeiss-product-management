using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProductManagement.Application.Products.Abstractions;
using ProductManagement.Infrastructure.Persistence;

namespace ProductManagement.Infrastructure.Products;

/// <summary>
/// Generates product identifiers from <see cref="ProductIdSequence"/>. One round trip per identifier.
/// </summary>
internal sealed class PostgresProductIdGenerator(ProductManagementDbContext context) : IProductIdGenerator
{
    // Constant SQL, no user input involved.
    private const string NextValueSql = $"SELECT nextval('{ProductIdSequence.Name}')::integer AS \"Value\"";

    // SQLSTATE raised by nextval() on a NO CYCLE sequence that reached its maximum.
    private const string SequenceGeneratorLimitExceeded = "2200H";

    public async Task<int> NextIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            // ToListAsync (not SingleAsync) keeps EF from wrapping the statement in a sub-query.
            var values = await context.Database.SqlQueryRaw<int>(NextValueSql).ToListAsync(cancellationToken);
            return values.Single();
        }
        catch (PostgresException exception)
            when (exception.SqlState == SequenceGeneratorLimitExceeded)
        {
            throw new InvalidOperationException(
                "All six-digit product identifiers have been assigned; the product id sequence is exhausted.",
                exception);
        }
    }
}
