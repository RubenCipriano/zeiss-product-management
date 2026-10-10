using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProductManagement.Core.Categories;
using ProductManagement.Core.Products;

namespace ProductManagement.Core;

/// <summary>
/// Tells the dependency-injection container which class to create for each Core interface. Called once at startup
/// from <c>Program.cs</c> (<c>services.AddCore()</c>).
/// </summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the use cases (one instance per HTTP request: <c>AddScoped</c>) and the request validators (stateless,
    /// so one instance for the whole app: singleton).
    /// </summary>
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<IProductQueryService>(
            ServiceLifetime.Singleton,
            includeInternalTypes: true);

        services.AddScoped<IProductQueryService, ProductQueryService>();
        services.AddScoped<IProductCommandService, ProductCommandService>();
        services.AddScoped<IStockCommandService, StockCommandService>();
        services.AddScoped<ICategoryQueryService, CategoryQueryService>();

        services.TryAddSingleton(TimeProvider.System);

        return services;
    }
}
