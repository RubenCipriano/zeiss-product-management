using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProductManagement.Domain.Products;

namespace ProductManagement.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
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

        // GIN trigram index: makes ILIKE '%fragment%' searches index-assisted instead of full scans.
        builder.HasIndex(product => product.Name)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");

        builder.HasIndex(product => product.StockQuantity);

        // B-tree for "all products ordered by name" (the default list): without it, a large catalogue is sorted on
        // every request. The trigram index above serves only the contains-search.
        builder.HasIndex(product => new { product.Name, product.Id })
            .HasDatabaseName("ix_products_name_id");
    }
}
