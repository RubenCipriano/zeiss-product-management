using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProductManagement.Domain.Products;

namespace ProductManagement.Infrastructure.Persistence.Configurations;

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public const string RequestKeyIndexName = "ix_stock_movements_request_key";

    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("stock_movements", table =>
            table.HasCheckConstraint("ck_stock_movements_stock_after_non_negative", "stock_after >= 0"));

        builder.HasKey(movement => movement.Id);

        // bigint identity: the ledger grows without bound (millions of rows), unlike the six-digit product ids.
        builder.Property(movement => movement.Id)
            .UseIdentityAlwaysColumn();

        builder.Property(movement => movement.Reason)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(movement => movement.PerformedBy)
            .HasMaxLength(StockMovement.PerformedByMaxLength)
            .IsRequired();

        builder.Property(movement => movement.RequestKey)
            .HasMaxLength(StockMovement.RequestKeyMaxLength);

        // Exactly-once, enforced by the database: one movement per idempotent request. A second execution of the same
        // request (its Redis claim lost or released after the commit) violates this index, and PostgreSQL rolls back
        // the whole statement, the stock change included. Partial: only requests that sent a key.
        builder.HasIndex(movement => movement.RequestKey)
            .IsUnique()
            .HasFilter("request_key IS NOT NULL")
            .HasDatabaseName(RequestKeyIndexName);

        // The history belongs to the product: deleting a product deletes its history.
        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(movement => movement.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        // Serves "history of product X, newest first" as an index range scan, even with millions of rows.
        builder.HasIndex(movement => new { movement.ProductId, movement.OccurredAt, movement.Id })
            .IsDescending(false, true, true);
    }
}
