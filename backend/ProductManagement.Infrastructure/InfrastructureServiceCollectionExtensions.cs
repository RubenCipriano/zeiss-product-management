using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using ProductManagement.Application.Categories.Abstractions;
using ProductManagement.Application.Common.Idempotency;
using ProductManagement.Application.Common.Persistence;
using ProductManagement.Application.Common.Security;
using ProductManagement.Application.Products.Abstractions;
using ProductManagement.Infrastructure.Caching;
using ProductManagement.Infrastructure.Categories;
using ProductManagement.Infrastructure.Persistence;
using ProductManagement.Infrastructure.Persistence.Seeding;
using ProductManagement.Infrastructure.Idempotency;
using ProductManagement.Infrastructure.Products;
using ProductManagement.Infrastructure.Security;

namespace ProductManagement.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public const string ReadinessHealthCheckTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{DatabaseOptions.ConnectionStringName}' is not configured.");
        }

        var databaseSection = configuration.GetSection(DatabaseOptions.SectionName);
        services.Configure<DatabaseOptions>(databaseSection);
        var databaseOptions = databaseSection.Get<DatabaseOptions>() ?? new DatabaseOptions();

        // Pooling reuses DbContext instances across requests, which lowers allocation cost under load.
        services.AddDbContextPool<ProductManagementDbContext>(options =>
        {
            options
                .UseNpgsql(
                    connectionString,
                    npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
                .UseSnakeCaseNamingConvention();

            // No EnableRetryOnFailure: the stock endpoints are not idempotent. If a connection dropped after the
            // UPDATE committed, an automatic retry would apply it twice. See docs/adr/0003.
            if (databaseOptions.SeedSampleData)
            {
                options
                    .UseSeeding((context, _) => SampleDataSeeder.Seed(context))
                    .UseAsyncSeeding((context, _, cancellationToken) =>
                        SampleDataSeeder.SeedAsync(context, cancellationToken));
            }
        });

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<ProductQueries>();
        services.AddScoped<IProductQueries, CachingProductQueries>();
        services.AddSingleton<SingleFlight>();
        services.AddScoped<IProductIdGenerator, PostgresProductIdGenerator>();
        services.AddScoped<ICategoryQueries, CategoryQueries>();

        var healthChecks = services.AddHealthChecks()
            .AddDbContextCheck<ProductManagementDbContext>("database", tags: [ReadinessHealthCheckTag]);

        AddCatalogCache(services, configuration, healthChecks);

        return services;
    }

    private static void AddCatalogCache(
        IServiceCollection services,
        IConfiguration configuration,
        IHealthChecksBuilder healthChecks)
    {
        services.Configure<CacheOptions>(configuration.GetSection(CacheOptions.SectionName));
        services.Configure<FailedAttemptThrottleOptions>(configuration.GetSection(FailedAttemptThrottleOptions.SectionName));
        services.Configure<IdempotencyOptions>(configuration.GetSection(IdempotencyOptions.SectionName));

        var redisConnectionString = configuration.GetConnectionString(CacheOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(redisConnectionString))
        {
            services.AddSingleton<ICatalogCache, NullCatalogCache>();
            services.AddSingleton<IFailedAttemptThrottle, InMemoryFailedAttemptThrottle>();
            services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
            services.AddSingleton<IDistributedRateLimiter, LocalOnlyRateLimiter>();
            return;
        }

        var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
        // Start even if Redis is down and keep reconnecting in the background; fail fast per call so a slow or
        // missing cache costs milliseconds, not seconds (the queries then fall back to the database).
        redisOptions.AbortOnConnectFail = false;
        redisOptions.ConnectTimeout = Math.Min(redisOptions.ConnectTimeout, 2_000);
        redisOptions.SyncTimeout = Math.Min(redisOptions.SyncTimeout, 500);
        redisOptions.AsyncTimeout = Math.Min(redisOptions.AsyncTimeout, 500);

        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
        services.AddSingleton<ICatalogCache, RedisCatalogCache>();
        services.AddSingleton<IFailedAttemptThrottle, RedisFailedAttemptThrottle>();
        services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        services.AddSingleton<IDistributedRateLimiter, RedisDistributedRateLimiter>();
        healthChecks.AddCheck<RedisHealthCheck>("cache", tags: [ReadinessHealthCheckTag]);
    }
}
