using Microsoft.EntityFrameworkCore;
using ProductManagement.Domain.Categories;
using ProductManagement.Domain.Products;
using ProductManagement.Infrastructure.Products;

namespace ProductManagement.Infrastructure.Persistence;

public sealed class ProductManagementDbContext(DbContextOptions<ProductManagementDbContext> options)
    : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Trigram index support for fast case-insensitive partial name search (ILIKE '%term%').
        modelBuilder.HasPostgresExtension("pg_trgm");

        ProductIdSequence.Configure(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ProductManagementDbContext).Assembly);
    }
}
