using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace ProductManagement.Testing;

/// <summary>
/// Hosts the real API in memory (full middleware pipeline, real DI graph) against the given database and cache.
/// Each instance is an independent "API replica" with its own DbContext pool and Redis connection, which lets
/// tests simulate several replicas sharing one database and one cache.
/// </summary>
public sealed class ProductManagementApiFactory(
    string databaseConnectionString,
    string? redisConnectionString,
    bool applyMigrations) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:ProductManagement", databaseConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", redisConnectionString ?? string.Empty);
        builder.UseSetting("Database:ApplyMigrationsOnStartup", applyMigrations.ToString());
        builder.UseSetting("Database:SeedSampleData", bool.FalseString);
        builder.UseSetting("OpenApi:Enabled", bool.TrueString);
        builder.UseSetting("Logging:LogLevel:Default", nameof(LogLevel.Warning));
        builder.UseSetting("Logging:Console:FormatterName", "simple");
    }
}
