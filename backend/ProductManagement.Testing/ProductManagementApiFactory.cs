using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ProductManagement.Testing;

/// <summary>
/// Hosts the real API in memory (full middleware pipeline, real DI graph) against the given database and cache.
/// Each instance is an independent "API replica" with its own DbContext pool and Redis connection, which lets
/// tests simulate several replicas sharing one database and one cache.
/// </summary>
public sealed class ProductManagementApiFactory(
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
        builder.UseSetting("Authentication:Authority", TestTokens.Issuer);
        builder.UseSetting("Authentication:Audience", TestTokens.Audience);

        // Functional tests fire bursts of parallel requests; rate limiting has its own dedicated tests.
        builder.UseSetting("RateLimiting:RequestsPerSecond", "100000");
        builder.UseSetting("RateLimiting:RequestBurst", "100000");
        builder.UseSetting("RateLimiting:WritesPerSecond", "100000");
        builder.UseSetting("RateLimiting:WriteBurst", "100000");

        builder.UseSetting("Logging:LogLevel:Default", nameof(LogLevel.Warning));
        builder.UseSetting("Logging:Console:FormatterName", "simple");

        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }

        // Trust the test signing key instead of downloading keys from an identity provider.
        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                configuration.SigningKeys.Add(TestTokens.SigningKey);

                // A static configuration manager: no discovery document or key download over HTTP.
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            }));
    }
}
