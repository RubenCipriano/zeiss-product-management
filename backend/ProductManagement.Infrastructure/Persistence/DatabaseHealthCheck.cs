using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace ProductManagement.Infrastructure.Persistence;

/// <summary>
/// Readiness check ("may Kubernetes send this replica traffic?"): can it reach PostgreSQL? Runs <c>SELECT 1</c> on a
/// dedicated one-connection pool.
/// </summary>
/// <remarks>
/// Why a pool of its own: a check that waits for a free connection in the busy request pool times out under load, and
/// Kubernetes then takes busy-but-healthy replicas out of service, several at once, exactly when they are needed. Its
/// timeouts stay below the probe's 3 s, so an unreachable database is reported as unhealthy rather than as a probe
/// timeout. See docs/adr/0018-autoscaling-and-scale-testing.md.
/// </remarks>
internal class DatabaseHealthCheck : IHealthCheck, IAsyncDisposable
{
    /// <summary>Budget of one check (registered as the health check's timeout), below the probe's 3 s.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly NpgsqlDataSource _dataSource;

    public DatabaseHealthCheck(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MinPoolSize = 0,
            MaxPoolSize = 1,
            Timeout = (int)Timeout.TotalSeconds,
            CommandTimeout = (int)Timeout.TotalSeconds,
        };
        _dataSource = NpgsqlDataSource.Create(builder);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // A failure or the timeout is reported as the registration's failure status (unhealthy), with the exception.
        await using var command = _dataSource.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
