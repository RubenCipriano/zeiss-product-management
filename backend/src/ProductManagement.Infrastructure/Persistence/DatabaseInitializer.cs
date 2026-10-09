using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProductManagement.Infrastructure.Persistence;

public static partial class DatabaseInitializer
{
    /// <summary>
    /// Applies pending migrations (and the seeding hooks) when <see cref="DatabaseOptions.ApplyMigrationsOnStartup"/>
    /// is enabled.
    /// </summary>
    /// <remarks>
    /// Safe with several instances starting together: EF Core takes a database-wide lock while migrating, so only
    /// one instance applies migrations and the rest wait, then find nothing left to do.
    /// </remarks>
    public static async Task InitializeDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (!options.ApplyMigrationsOnStartup)
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ProductManagementDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(DatabaseInitializer));

        LogApplyingMigrations(logger);
        await context.Database.MigrateAsync(cancellationToken);
        LogDatabaseReady(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying database migrations")]
    private static partial void LogApplyingMigrations(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database is up to date")]
    private static partial void LogDatabaseReady(ILogger logger);
}
