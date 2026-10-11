using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ProductManagement.Infrastructure.Persistence;

/// <summary>
/// The database step of the API's startup, called from <c>Program.cs</c> before the API serves requests: brings the
/// schema up to date (<see cref="DatabaseMigrator"/>) when <see cref="DatabaseOptions.ApplyMigrationsOnStartup"/> is
/// on.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (options.ApplyMigrationsOnStartup)
        {
            await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(cancellationToken);
        }
    }
}
