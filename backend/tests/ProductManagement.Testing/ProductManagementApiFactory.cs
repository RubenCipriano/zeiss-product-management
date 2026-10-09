using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace ProductManagement.Testing;

/// <summary>
/// Hosts the real API in memory (full middleware pipeline, real DI graph) against the given database.
/// Each instance is an independent "API instance" with its own DbContext pool, which lets tests simulate
/// several instances sharing one database.
/// </summary>
public sealed class ProductManagementApiFactory(string connectionString, bool applyMigrations)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:ProductManagement", connectionString);
        builder.UseSetting("Database:ApplyMigrationsOnStartup", applyMigrations.ToString());
        builder.UseSetting("Database:SeedSampleData", bool.FalseString);
        builder.UseSetting("OpenApi:Enabled", bool.TrueString);
        builder.UseSetting("Logging:LogLevel:Default", nameof(LogLevel.Warning));
        builder.UseSetting("Logging:Console:FormatterName", "simple");
    }
}
