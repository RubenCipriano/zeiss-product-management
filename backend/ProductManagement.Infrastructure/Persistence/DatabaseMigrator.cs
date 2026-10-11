using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ProductManagement.Infrastructure.Caching;

namespace ProductManagement.Infrastructure.Persistence;

/// <summary>
/// Brings the database schema up to date when the API starts, before it serves its first request: applies the pending
/// EF Core migrations (the versioned schema changes in <c>Migrations/</c>) and then the optional sample data.
/// </summary>
/// <remarks>
/// <para>
/// Who: the schema owner. It connects with its own short-lived context and connections, as
/// <see cref="DatabaseOptions.MigratorUsername"/> when that is set (<c>pm_migrator</c> in Kubernetes), while the
/// requests keep using the connection string's user (<c>pm_app</c>, no right to change the schema).
/// </para>
/// <para>
/// Several replicas starting at once (a rollout, a scale-up) take turns: the first takes a PostgreSQL advisory lock
/// for the whole run (migrations and seeding), the others wait for it and then find nothing left to do. EF Core's own
/// migration lock is not enough on PostgreSQL: Npgsql implements it as a table lock that ends with each migration's
/// transaction, so a second replica can slip in between two migrations and apply one again (measured: a duplicate key
/// in the migration history). The advisory lock belongs to the session, so it is held across those transactions, and
/// it is released by PostgreSQL itself if the replica dies.
/// </para>
/// </remarks>
internal partial class DatabaseMigrator(
    string connectionString,
    IOptions<DatabaseOptions> options,
    TimeProvider timeProvider,
    ICatalogCache cache,
    ILogger<DatabaseMigrator> logger)
{
    /// <summary>
    /// How long one migration statement, or the wait for another replica's migrations, may take. The same budget as
    /// the API's startup probe in Kubernetes (the deploy repository): a replica that has not started by then is
    /// restarted anyway.
    /// </summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The advisory lock's key. Any fixed number works: advisory locks are per database, and nothing else in this one
    /// uses them.
    /// </summary>
    private const long MigrationLockKey = 0x504D_4D49_4752;   // "PMMIGR"

    private const int MaxConnectAttempts = 30;

    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromSeconds(2);

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var connection = MigrationConnectionString(connectionString, settings);
        var contextOptions = new DbContextOptionsBuilder<ProductManagementDbContext>();
        InfrastructureServiceCollectionExtensions.ConfigureDbContext(
            contextOptions, connection.ConnectionString, settings.SeedSampleData, timeProvider);
        await using var context = new ProductManagementDbContext(contextOptions.Options);

        await WaitForDatabaseAsync(context, cancellationToken);

        var started = timeProvider.GetTimestamp();
        var user = connection.Username ?? "the default user";
        LogMigratingAs(logger, user);
        await using (await AcquireMigrationLockAsync(connection.ConnectionString, cancellationToken))
        {
            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                LogApplyingMigrations(logger, pending.Count, pending);
            }

            // Applies the pending migrations, each in its own transaction, then runs the seeding hooks (sample data).
            await context.Database.MigrateAsync(cancellationToken);

            // The data may have changed underneath the read cache.
            if (pending.Count > 0 || settings.SeedSampleData)
            {
                await cache.InvalidateAsync(CatalogChange.Products, []);
            }

            var seconds = timeProvider.GetElapsedTime(started).TotalSeconds;
            LogDatabaseUpToDate(logger, pending.Count, user, seconds);
        }
    }

    /// <summary>
    /// The connection the migrations use: the API's connection string, as the migrator when one is configured. No
    /// pooling, so its connections are closed (and the advisory lock released) as soon as the migration step ends,
    /// instead of idling in a pool for the lifetime of the API.
    /// </summary>
    internal static NpgsqlConnectionStringBuilder MigrationConnectionString(
        string connectionString,
        DatabaseOptions options)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            CommandTimeout = (int)Timeout.TotalSeconds,
        };
        if (!string.IsNullOrEmpty(options.MigratorUsername))
        {
            builder.Username = options.MigratorUsername;
            builder.Password = options.MigratorPassword;
        }

        return builder;
    }

    /// <summary>
    /// Takes the migration lock on a connection of its own and returns that connection: disposing it ends the session,
    /// which releases the lock.
    /// </summary>
    /// <remarks>
    /// A replica that finds the lock taken asks again every 2 s instead of blocking in <c>pg_advisory_lock</c>: a
    /// statement blocked there keeps its snapshot open, and <c>CREATE INDEX CONCURRENTLY</c> in the other replica's
    /// migration waits for every older snapshot to end, so each would wait for the other forever (measured).
    /// </remarks>
    private async Task<NpgsqlConnection> AcquireMigrationLockAsync(
        string migrationConnectionString,
        CancellationToken cancellationToken)
    {
        var lockConnection = new NpgsqlConnection(migrationConnectionString);
        try
        {
            await lockConnection.OpenAsync(cancellationToken);
            await using var tryLock = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", lockConnection);
            tryLock.Parameters.AddWithValue("key", MigrationLockKey);

            var deadline = timeProvider.GetUtcNow() + Timeout;
            for (var attempt = 1; await tryLock.ExecuteScalarAsync(cancellationToken) is not true; attempt++)
            {
                if (timeProvider.GetUtcNow() > deadline)
                {
                    throw new TimeoutException(
                        $"Another instance has been migrating the database for over {Timeout.TotalMinutes} minutes.");
                }

                if (attempt == 1)
                {
                    LogWaitingForMigrationLock(logger);
                }

                await Task.Delay(LockRetryDelay, timeProvider, cancellationToken);
            }

            return lockConnection;
        }
        catch
        {
            await lockConnection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The API (a new pod, or the whole system after a restart) can start before the database server has: wait for it
    /// instead of failing.
    /// </summary>
    private async Task WaitForDatabaseAsync(ProductManagementDbContext context, CancellationToken cancellationToken)
    {
        for (var attempt = 1; !await context.Database.CanConnectAsync(cancellationToken); attempt++)
        {
            if (attempt == MaxConnectAttempts)
            {
                throw new InvalidOperationException(
                    $"The database is still unreachable after {MaxConnectAttempts} attempts (or the migrator's user " +
                    "or password is wrong).");
            }

            LogWaitingForDatabase(logger, attempt, MaxConnectAttempts);
            await Task.Delay(ConnectRetryDelay, timeProvider, cancellationToken);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Waiting for the database (attempt {Attempt} of {MaxAttempts})")]
    private static partial void LogWaitingForDatabase(ILogger logger, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Checking database migrations as {User}")]
    private static partial void LogMigratingAs(ILogger logger, string user);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Another instance is migrating the database; waiting for it to finish")]
    private static partial void LogWaitingForMigrationLock(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {Count} database migrations: {Migrations}")]
    private static partial void LogApplyingMigrations(ILogger logger, int count, IEnumerable<string> migrations);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Database is up to date ({Applied} migrations applied as {User}, {Seconds:0.0} s)")]
    private static partial void LogDatabaseUpToDate(ILogger logger, int applied, string user, double seconds);
}
