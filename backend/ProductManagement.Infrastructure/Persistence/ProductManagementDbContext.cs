using Microsoft.EntityFrameworkCore;
using ProductManagement.Core.Categories;
using ProductManagement.Core.Products;
using ProductManagement.Infrastructure.Products;

namespace ProductManagement.Infrastructure.Persistence;

/// <summary>
/// EF Core's entry point to the database: one property per table, plus the mapping configuration (in
/// <c>Configurations/</c>) applied when the model is built. One instance per request, reused from a pool.
/// </summary>
public class ProductManagementDbContext(DbContextOptions<ProductManagementDbContext> options)
    : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Trigram index support for fast case-insensitive partial name search (ILIKE '%term%').
        modelBuilder.HasPostgresExtension("pg_trgm");

        ProductIdSequence.Configure(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ProductManagementDbContext).Assembly);
    }
}
