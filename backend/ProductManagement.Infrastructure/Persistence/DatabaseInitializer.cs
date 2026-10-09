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

        LogApplyingMigrations(logger);
        await context.Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<ICatalogCache>().InvalidateAsync();
        LogDatabaseReady(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying database migrations")]
    private static partial void LogApplyingMigrations(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database is up to date")]
    private static partial void LogDatabaseReady(ILogger logger);
}
