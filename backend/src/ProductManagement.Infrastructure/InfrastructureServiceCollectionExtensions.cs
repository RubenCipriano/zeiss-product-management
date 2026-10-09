using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProductManagement.Application.Categories.Abstractions;
using ProductManagement.Application.Common.Persistence;
using ProductManagement.Application.Products.Abstractions;
using ProductManagement.Infrastructure.Categories;
using ProductManagement.Infrastructure.Persistence;
using ProductManagement.Infrastructure.Persistence.Seeding;
using ProductManagement.Infrastructure.Products;

namespace ProductManagement.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public const string DatabaseHealthCheckTag = "ready";

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
        services.AddScoped<IProductQueries, ProductQueries>();
        services.AddScoped<IProductIdGenerator, PostgresProductIdGenerator>();
        services.AddScoped<ICategoryQueries, CategoryQueries>();

        services.AddHealthChecks()
            .AddDbContextCheck<ProductManagementDbContext>("database", tags: [DatabaseHealthCheckTag]);

        return services;
    }
}
