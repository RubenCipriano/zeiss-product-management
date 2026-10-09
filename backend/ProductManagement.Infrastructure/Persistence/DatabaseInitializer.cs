using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductManagement.Infrastructure.Caching;

namespace ProductManagement.Infrastructure.Persistence;

public static partial class DatabaseInitializer
{
    /// <summary>
    /// Applies pending migrations at startup when <see cref="DatabaseOptions.ApplyMigrationsOnStartup"/> is enabled
    /// (local development). In Kubernetes migrations run once, in a Job, via <see cref="MigrateDatabaseAsync"/>.
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
        await scope.ServiceProvider.GetRequiredService<ICatalogCache>().InvalidateAsync();
        LogDatabaseReady(logger);
    }

    /// <summary>
    /// In Kubernetes the migration Job can start before PostgreSQL accepts connections; wait for it instead of failing.
    /// </summary>
    private static async Task WaitForDatabaseAsync(
        ProductManagementDbContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 30;
        for (var attempt = 1; !await context.Database.CanConnectAsync(cancellationToken); attempt++)
        {
            if (attempt == maxAttempts)
            {
                throw new InvalidOperationException($"The database is still unreachable after {maxAttempts} attempts.");
            }

            LogWaitingForDatabase(logger, attempt, maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting for the database (attempt {Attempt} of {MaxAttempts})")]
    private static partial void LogWaitingForDatabase(ILogger logger, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying database migrations")]
    private static partial void LogApplyingMigrations(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database is up to date")]
    private static partial void LogDatabaseReady(ILogger logger);
}
