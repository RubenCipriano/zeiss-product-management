using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductManagement.Infrastructure.Caching;

namespace ProductManagement.Infrastructure.Persistence;

/// <summary>
/// Brings the database schema up to date by applying EF Core migrations (the versioned schema changes in
/// <c>Migrations/</c>): at API startup in local development, or once per deployment via the API image's
/// <c>migrate</c> command.
/// </summary>
public static partial class DatabaseInitializer
{
    private const int MaxConnectAttempts = 30;

    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Applies pending migrations at startup when <see cref="DatabaseOptions.ApplyMigrationsOnStartup"/> is enabled
    /// (local development). Deployments instead run <see cref="MigrateDatabaseAsync"/> once, through the
    /// <c>migrate</c> command, before the rollout.
    /// </summary>
    public static async Task InitializeDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (options.ApplyMigrationsOnStartup)
        {
            await services.MigrateDatabaseAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Applies pending migrations and the seeding hooks, then invalidates the read cache (the data may have changed
    /// underneath it).
    /// </summary>
    /// <remarks>
    /// Safe to run concurrently: EF Core takes a database-wide lock while migrating, so only one caller applies the
    /// migrations and the others wait, then find nothing left to do.
    /// </remarks>
    public static async Task MigrateDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ProductManagementDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(DatabaseInitializer));

        await WaitForDatabaseAsync(context, logger, cancellationToken);

        LogApplyingMigrations(logger);
        await context.Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<ICatalogCache>().InvalidateAsync(CatalogChange.Products, []);
        LogDatabaseReady(logger);
    }

    /// <summary>
    /// The <c>migrate</c> command can start while the database server is still starting or restarting; wait for it
    /// instead of failing.
    /// </summary>
    private static async Task WaitForDatabaseAsync(
        ProductManagementDbContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; !await context.Database.CanConnectAsync(cancellationToken); attempt++)
        {
            if (attempt == MaxConnectAttempts)
            {
                throw new InvalidOperationException(
                    $"The database is still unreachable after {MaxConnectAttempts} attempts.");
            }

            LogWaitingForDatabase(logger, attempt, MaxConnectAttempts);
            await Task.Delay(ConnectRetryDelay, cancellationToken);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Waiting for the database (attempt {Attempt} of {MaxAttempts})")]
    private static partial void LogWaitingForDatabase(ILogger logger, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying database migrations")]
    private static partial void LogApplyingMigrations(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database is up to date")]
    private static partial void LogDatabaseReady(ILogger logger);
}
