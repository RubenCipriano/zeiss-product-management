using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using ProductManagement.Api.Security;

namespace ProductManagement.Testing;

/// <summary>
/// Hosts the real API in memory (full middleware pipeline, real DI graph) against the given database and cache.
/// Each instance is an independent "API replica" with its own DbContext pool and Redis connection, which lets
/// tests simulate several replicas sharing one database and one cache.
/// </summary>
public class ProductManagementApiFactory(
    string databaseConnectionString,
    string? redisConnectionString,
    bool applyMigrations,
    IReadOnlyDictionary<string, string>? settings = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:ProductManagement", databaseConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", redisConnectionString ?? string.Empty);
        builder.UseSetting("Database:ApplyMigrationsOnStartup", applyMigrations.ToString());
        builder.UseSetting("Database:SeedSampleData", bool.FalseString);
        builder.UseSetting("OpenApi:Enabled", bool.TrueString);
        builder.UseSetting("Authentication:SigningKey", TestTokens.SigningKeyPem);
        builder.UseSetting("Authentication:Consumers:0:ClientId", TestTokens.ConsumerId);
        builder.UseSetting("Authentication:Consumers:0:SecretHash", ConsumerSecretHash.Value);
        builder.UseSetting("Authentication:Consumers:0:Roles:0", TestTokens.Viewer);

        // Functional tests fire bursts of parallel requests; rate limiting has its own dedicated tests.
        builder.UseSetting("RateLimiting:RequestsPerSecond", "100000");
        builder.UseSetting("RateLimiting:RequestBurst", "100000");
        builder.UseSetting("RateLimiting:WritesPerSecond", "100000");
        builder.UseSetting("RateLimiting:WriteBurst", "100000");
        builder.UseSetting("RateLimiting:TokenRequestsPerMinute", "100000");

        builder.UseSetting("Logging:LogLevel:Default", nameof(LogLevel.Warning));
        builder.UseSetting("Logging:Console:FormatterName", "simple");

        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }
    }

    // PBKDF2 is deliberately slow: hash once per test run, not once per host.
    private static readonly Lazy<string> ConsumerSecretHash = new(() => SecretHasher.Hash(TestTokens.ConsumerSecret));
}
