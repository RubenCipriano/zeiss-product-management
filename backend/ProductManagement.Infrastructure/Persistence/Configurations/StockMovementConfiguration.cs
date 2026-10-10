using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ProductManagement.Core.Products;

namespace ProductManagement.Infrastructure.Persistence.Configurations;

/// <summary>
/// How <see cref="StockMovement"/> (the stock ledger) maps to the <c>stock_movements</c> table, including the unique
/// index that makes the database refuse to apply the same request twice. EF Core reads this configuration at startup,
/// and migrations are generated from it.
/// </summary>
internal class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public const string RequestKeyIndexName = "ix_stock_movements_request_key";

    /// <summary>The reason is stored as its name ("StockRemoved"); 20 characters leaves room for new ones.</summary>
    private const int ReasonMaxLength = 20;

    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("stock_movements", table =>
            table.HasCheckConstraint("ck_stock_movements_stock_after_non_negative", "stock_after >= 0"));

        // The ledger only grows. By default autovacuum would wait for 20% new rows (4 million at 20 million), and until
        // then reading the newest history (the busiest products) is slower. Vacuum every 100,000 new movements
        // instead; on an insert-only table it only visits the new pages.
        // Measured in docs/adr/0022-database-at-scale.md.
        builder.HasStorageParameter("autovacuum_vacuum_insert_scale_factor", 0);
        builder.HasStorageParameter("autovacuum_vacuum_insert_threshold", 100000);

        builder.HasKey(movement => movement.Id);

        // bigint identity: the ledger grows without bound (millions of rows), unlike the six-digit product ids.
        builder.Property(movement => movement.Id)
            .UseIdentityAlwaysColumn();

        builder.Property(movement => movement.Reason)
            .HasConversion<string>()
            .HasMaxLength(ReasonMaxLength);

        builder.Property(movement => movement.PerformedBy)
            .HasMaxLength(StockMovement.PerformedByMaxLength)
            .IsRequired();

        builder.Property(movement => movement.RequestKey)
            .HasMaxLength(StockMovement.RequestKeyMaxLength);

        // Exactly-once, enforced by the database: one movement per idempotency key. A second execution of the same
        // request (its Redis claim lost or released after the commit) violates this index, and PostgreSQL rolls back
        // the whole statement, the stock change included. Partial index: only rows whose request sent a key.
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
