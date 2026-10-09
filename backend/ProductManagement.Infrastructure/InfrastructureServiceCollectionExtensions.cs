using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ProductManagement.Application.Categories.Abstractions;
using ProductManagement.Application.Common.Persistence;
using ProductManagement.Application.Products.Abstractions;
using ProductManagement.Infrastructure.Caching;
using ProductManagement.Infrastructure.Categories;
using ProductManagement.Infrastructure.Edge;
using ProductManagement.Infrastructure.Idempotency;
using ProductManagement.Infrastructure.Persistence;
using ProductManagement.Infrastructure.Persistence.Seeding;
using ProductManagement.Infrastructure.Products;
using ProductManagement.Infrastructure.Redis;
using ProductManagement.Infrastructure.Security;
using StackExchange.Redis;

namespace ProductManagement.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public const string ReadinessHealthCheckTag = "ready";

    /// <param name="requireRedis">
    /// Refuse to start without the <c>Redis</c> connection string. Without Redis the cache is off and idempotency keys,
    /// rate limits and failed sign-ins are only known to this process: fine for one local instance, wrong for replicas.
    /// </param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool requireRedis = false)
    {
        var connectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{DatabaseOptions.ConnectionStringName}' is not configured.");
        }

        services.AddOptions<DatabaseOptions>()
            .BindConfiguration(DatabaseOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Pooling reuses DbContext instances across requests, which lowers allocation cost under load.
        services.AddDbContextPool<ProductManagementDbContext>((provider, options) =>
        {
            options
                .UseNpgsql(
                    connectionString,
                    npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
                .UseSnakeCaseNamingConvention();

            // No EnableRetryOnFailure: the stock endpoints are not idempotent. If a connection dropped after the
            // UPDATE committed, an automatic retry would apply it twice. See docs/adr/0003.
            if (provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.SeedSampleData)
            {
                options
                    .UseSeeding((context, _) => SampleDataSeeder.Seed(context))
                    .UseAsyncSeeding((context, _, cancellationToken) =>
                        SampleDataSeeder.SeedAsync(context, cancellationToken));
            }
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ProductQueries>();
        services.AddScoped<IProductIdGenerator, PostgresProductIdGenerator>();
        services.AddScoped<ICategoryQueries, CategoryQueries>();

        var healthChecks = services.AddHealthChecks()
            .AddDbContextCheck<ProductManagementDbContext>("database", tags: [ReadinessHealthCheckTag]);

        var redisConfigured = AddRedis(services, configuration, healthChecks, requireRedis);
        AddCatalogCache(services, redisConfigured);
        AddIdempotencyStore(services, redisConfigured);
        AddSecurityStores(services, redisConfigured);

        return services;
    }

    /// <summary>The shared connection, when Redis is configured. Returns whether it is.</summary>
    private static bool AddRedis(
        IServiceCollection services,
        IConfiguration configuration,
        IHealthChecksBuilder healthChecks,
        bool requireRedis)
    {
        services.AddOptions<RedisOptions>()
            .BindConfiguration(RedisOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var connectionString = configuration.GetConnectionString(RedisOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return requireRedis
                ? throw new InvalidOperationException(
                    $"Connection string '{RedisOptions.ConnectionStringName}' is not configured. Outside development the " +
                    "API needs Redis: without it, idempotency keys, rate limits and failed sign-ins are per replica.")
                : false;
        }

        // Parsed here so a malformed connection string fails at startup.
        var redisConfiguration = ConfigurationOptions.Parse(connectionString);
        services.AddSingleton<IConnectionMultiplexer>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<RedisOptions>>().Value;

            // Start even if Redis is down and keep reconnecting in the background; fail fast per call so a slow or
            // missing Redis costs milliseconds, not seconds (every user of it then degrades).
            redisConfiguration.AbortOnConnectFail = false;
            redisConfiguration.ConnectTimeout = Math.Min(redisConfiguration.ConnectTimeout, Milliseconds(options.ConnectTimeout));
            redisConfiguration.SyncTimeout = Math.Min(redisConfiguration.SyncTimeout, Milliseconds(options.OperationTimeout));
            redisConfiguration.AsyncTimeout = Math.Min(redisConfiguration.AsyncTimeout, Milliseconds(options.OperationTimeout));
            return ConnectionMultiplexer.Connect(redisConfiguration);
        });
        healthChecks.AddCheck<RedisHealthCheck>("cache", tags: [ReadinessHealthCheckTag]);
        return true;
    }

    /// <summary>
    /// Caching is entirely decorators: reads go through <see cref="CachingProductQueries"/>, and writes invalidate
    /// through <see cref="CacheInvalidatingUnitOfWork"/> and <see cref="CacheInvalidatingProductRepository"/>. Without
    /// Redis the cache is a no-op and the decorators pass straight through.
    /// </summary>
    private static void AddCatalogCache(IServiceCollection services, bool redisConfigured)
    {
        services.AddOptions<CacheOptions>()
            .BindConfiguration(CacheOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        if (redisConfigured)
        {
            services.AddSingleton<ICatalogCache, RedisCatalogCache>();
        }
        else
        {
            services.AddSingleton<ICatalogCache, NullCatalogCache>();
        }

        services.AddSingleton<SingleFlight>();
        services.AddScoped<IProductQueries, CachingProductQueries>();

        services.AddScoped<UnitOfWork>();
        services.AddScoped<IUnitOfWork>(provider => new CacheInvalidatingUnitOfWork(
            provider.GetRequiredService<UnitOfWork>(),
            provider.GetRequiredService<ProductManagementDbContext>(),
            provider.GetRequiredService<ICatalogCache>()));

        services.AddScoped<ProductRepository>();
        services.AddScoped<IProductRepository>(provider => new CacheInvalidatingProductRepository(
            provider.GetRequiredService<ProductRepository>(),
            provider.GetRequiredService<ICatalogCache>()));
    }

    private static void AddIdempotencyStore(IServiceCollection services, bool redisConfigured)
    {
        services.AddOptions<IdempotencyOptions>()
            .BindConfiguration(IdempotencyOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        if (redisConfigured)
        {
            services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        }
        else
        {
            services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        }
    }

    /// <summary>The shared state behind the token endpoint's throttle and the per-user rate limits.</summary>
    private static void AddSecurityStores(IServiceCollection services, bool redisConfigured)
    {
        services.AddOptions<FailedAttemptThrottleOptions>()
            .BindConfiguration(FailedAttemptThrottleOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        if (redisConfigured)
        {
            services.AddSingleton<IFailedAttemptThrottle, RedisFailedAttemptThrottle>();
            services.AddSingleton<IDistributedRateLimiter, RedisDistributedRateLimiter>();
        }
        else
        {
            services.AddSingleton<IFailedAttemptThrottle, InMemoryFailedAttemptThrottle>();
            services.AddSingleton<IDistributedRateLimiter, LocalOnlyRateLimiter>();
        }
    }

    private static int Milliseconds(TimeSpan value) => (int)Math.Ceiling(value.TotalMilliseconds);
}
