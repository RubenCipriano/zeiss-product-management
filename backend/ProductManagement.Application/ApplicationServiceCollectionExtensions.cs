using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProductManagement.Application.Categories;
using ProductManagement.Application.Products;

namespace ProductManagement.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
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
