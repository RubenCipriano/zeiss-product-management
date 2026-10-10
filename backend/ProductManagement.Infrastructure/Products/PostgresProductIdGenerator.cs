using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProductManagement.Core.Common.Persistence;
using ProductManagement.Core.Products.Abstractions;
using ProductManagement.Infrastructure.Persistence;

namespace ProductManagement.Infrastructure.Products;

/// <summary>
/// Hands out product ids from the PostgreSQL sequence (<see cref="ProductIdSequence"/>): one <c>nextval()</c> call
/// per id.
/// </summary>
internal class PostgresProductIdGenerator(ProductManagementDbContext context) : IProductIdGenerator
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
            throw new IdentifiersExhaustedException(
                "All six-digit product identifiers have been assigned; the product id sequence is exhausted.",
                exception);
        }
    }
}
