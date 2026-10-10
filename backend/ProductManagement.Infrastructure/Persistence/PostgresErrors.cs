using Npgsql;
using ProductManagement.Infrastructure.Persistence.Configurations;

namespace ProductManagement.Infrastructure.Persistence;

/// <summary>Recognises the PostgreSQL errors the code reacts to, instead of treating them as unexpected.</summary>
internal static class PostgresErrors
{
    /// <summary>
    /// The request's idempotency key is already recorded on a ledger row: this request was applied before (see
    /// docs/adr/0017).
    /// </summary>
    public static bool IsDuplicateRequestKey(PostgresException exception) =>
        exception is
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: StockMovementConfiguration.RequestKeyIndexName,
        };
}
