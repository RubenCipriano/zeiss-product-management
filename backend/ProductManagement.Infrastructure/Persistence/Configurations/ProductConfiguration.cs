using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProductManagement.Core.Products;

namespace ProductManagement.Infrastructure.Persistence.Configurations;

/// <summary>
/// How <see cref="Product"/> maps to the <c>products</c> table: columns, check constraints, indexes and autovacuum
/// settings. EF Core reads this configuration at startup, and migrations are generated from it.
/// </summary>
internal class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", table =>
        {
            // The database re-checks the domain rules, so no write path (including raw SQL) can break them.
            table.HasCheckConstraint(
                "ck_products_id_six_digits",
                $"id BETWEEN {ProductRules.MinId} AND {ProductRules.MaxId}");
            table.HasCheckConstraint(
                "ck_products_price_range",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"price BETWEEN {ProductRules.MinPrice} AND {ProductRules.MaxPrice}"));
            table.HasCheckConstraint(
                "ck_products_stock_quantity_range",
                $"stock_quantity BETWEEN 0 AND {ProductRules.MaxStockQuantity}");
        });

        // Autovacuum (PostgreSQL's background clean-up) runs much sooner than its default of 20% of the rows: after
        // ~5,000 changed rows. Every stock change rewrites a product row, and until vacuum has run, the fast
        // index-only reads behind counts and list pages must also visit the table. The table is small, so a vacuum
        // costs seconds. Measured in ADR 0022 (database at scale).
        // Fractions as strings: the provider formats a double with the current culture ("0,005" in Portuguese).
        builder.HasStorageParameter("autovacuum_vacuum_scale_factor", "0.005");
        builder.HasStorageParameter("autovacuum_vacuum_threshold", 1000);
        builder.HasStorageParameter("autovacuum_analyze_scale_factor", "0.02");

        builder.HasKey(product => product.Id);

        // Identifiers are assigned by the application from a database sequence (see ProductIdSequence).
        builder.Property(product => product.Id)
            .ValueGeneratedNever();

        builder.Property(product => product.Name)
            .HasMaxLength(ProductRules.NameMaxLength)
            .IsRequired();

        builder.Property(product => product.Description)
            .HasMaxLength(ProductRules.DescriptionMaxLength);

        // 1,000,000.00 needs 7 integer digits + 2 fractional digits.
        builder.Property(product => product.Price)
            .HasPrecision(9, ProductRules.PriceDecimalPlaces);

        builder.Property(product => product.StockQuantity);

        builder.Property(product => product.CreatedAt);

        builder.Property(product => product.UpdatedAt);

        // Maps to PostgreSQL's built-in `xmin` system column: a row version that changes on every update,
        // including the atomic stock UPDATE statements, at no storage cost.
        builder.Property(product => product.Version)
            .IsRowVersion();

        builder.HasOne(product => product.Category)
            .WithMany()
            .HasForeignKey(product => product.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // GIN trigram index (pg_trgm splits names into 3-letter pieces): lets the case-insensitive contains-search
        // (ILIKE '%fragment%') use an index instead of reading every row.
        builder.HasIndex(product => product.Name)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");

        // stock-level?min=&max= filters on stock and orders by (stock, name, id): with all three in one index, a page
        // is read in order from the index alone, with no sort. Measured in ADR 0022 (database at scale).
        builder.HasIndex(product => new { product.StockQuantity, product.Name, product.Id })
            .HasDatabaseName("ix_products_stock_quantity_name_id");

        // B-tree for "all products ordered by name" (the default list): without it, a large catalogue is sorted on
        // every request. The trigram index above serves only the contains-search.
        builder.HasIndex(product => new { product.Name, product.Id })
            .HasDatabaseName("ix_products_name_id");
    }
}
