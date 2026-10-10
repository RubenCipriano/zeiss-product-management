using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ProductManagement.Core.Categories.Abstractions;
using ProductManagement.Core.Common.Persistence;
using ProductManagement.Core.Products.Abstractions;
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

/// <summary>
/// Registers everything Infrastructure provides (PostgreSQL access, the cache, the Redis-backed stores) in the
/// dependency-injection container. Called once at startup from <c>Program.cs</c> (<c>AddInfrastructure</c>). This is
/// also where each feature picks its Redis or its in-memory version, depending on whether Redis is configured.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    public const string ReadinessHealthCheckTag = "ready";

    /// <summary>The Redis password, when it is not in the connection string (environment variable Redis__Password).</summary>
    private const string RedisPasswordKey = "Redis:Password";

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

        AddDatabase(services, connectionString);

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ProductQueries>();
        services.AddScoped<IProductIdGenerator, PostgresProductIdGenerator>();
        services.AddScoped<ICategoryQueries, CategoryQueries>();

        services.AddSingleton(new DatabaseHealthCheck(connectionString));
        var healthChecks = services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(
                "database",
                tags: [ReadinessHealthCheckTag],
                timeout: DatabaseHealthCheck.Timeout);

        var redisConfigured = AddRedis(services, configuration, healthChecks, requireRedis);
        AddCatalogCache(services, redisConfigured);
        AddIdempotencyStore(services, redisConfigured);
        AddSecurityStores(services, redisConfigured);

        return services;
    }

    /// <summary>The EF Core database context for PostgreSQL, and the optional sample data.</summary>
    private static void AddDatabase(IServiceCollection services, string connectionString)
    {
        services.AddOptions<DatabaseOptions>()
            .BindConfiguration(DatabaseOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Pooling reuses DbContext instances across requests, which lowers allocation cost under load.
        services.AddDbContextPool<ProductManagementDbContext>((provider, options) =>
        {
            // No EnableRetryOnFailure: the stock endpoints are not idempotent. If a connection dropped after the
            // UPDATE committed, an automatic retry would apply it twice. See docs/adr/0003.
            options
                .UseNpgsql(
                    connectionString,
                    npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
                .UseSnakeCaseNamingConvention();

            if (provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.SeedSampleData)
            {
                var seeder = new SampleDataSeeder(provider.GetRequiredService<TimeProvider>());
                options
                    .UseSeeding((context, _) => seeder.Seed(context))
                    .UseAsyncSeeding((context, _, cancellationToken) => seeder.SeedAsync(context, cancellationToken));
            }
        });
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
                    $"Connection string '{RedisOptions.ConnectionStringName}' is not configured. " +
                    "Outside development the API needs Redis: without it, idempotency keys, rate limits and " +
                    "failed sign-ins are per replica.")
                : false;
        }

        // Parsed here so a malformed connection string fails at startup.
        var redisConfiguration = ParseRedisConfiguration(connectionString, configuration[RedisPasswordKey]);
        services.AddSingleton<IConnectionMultiplexer>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<RedisOptions>>().Value;
            var connectTimeout = Milliseconds(options.ConnectTimeout);
            var operationTimeout = Milliseconds(options.OperationTimeout);

            // Start even if Redis is down and keep reconnecting in the background; fail fast per call so a slow or
            // missing Redis costs milliseconds, not seconds (every user of it then degrades).
            redisConfiguration.AbortOnConnectFail = false;
            redisConfiguration.ConnectTimeout = Math.Min(redisConfiguration.ConnectTimeout, connectTimeout);
            redisConfiguration.SyncTimeout = Math.Min(redisConfiguration.SyncTimeout, operationTimeout);
            redisConfiguration.AsyncTimeout = Math.Min(redisConfiguration.AsyncTimeout, operationTimeout);
            return ConnectionMultiplexer.Connect(redisConfiguration);
        });
        healthChecks.AddCheck<RedisHealthCheck>("cache", tags: [ReadinessHealthCheckTag]);
        return true;
    }

    /// <summary>
    /// Reads the Redis connection string. The password can also be configured on its own (<c>Redis:Password</c>, e.g.
    /// the environment variable <c>Redis__Password</c> from a Kubernetes Secret), so the connection string holds no
    /// secret and can sit in plain configuration. When it is set it wins over a password inside the connection string;
    /// when it is not, the connection string is used as it is.
    /// </summary>
    internal static ConfigurationOptions ParseRedisConfiguration(string connectionString, string? password)
    {
        var redisConfiguration = ConfigurationOptions.Parse(connectionString);
        if (!string.IsNullOrEmpty(password))
        {
            redisConfiguration.Password = password;
        }

        return redisConfiguration;
    }

    /// <summary>
    /// Caching is added purely with decorators (wrappers that implement the same interface): reads go through
    /// <see cref="CachingProductQueries"/>, and writes invalidate through <see cref="CacheInvalidatingUnitOfWork"/> and
    /// <see cref="CacheInvalidatingProductRepository"/>. Without Redis the cache is a no-op and the decorators pass
    /// straight through.
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
