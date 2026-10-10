using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProductManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutovacuumAtScale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Storage parameters only (ALTER TABLE ... SET): metadata, instant on any size, and the lock it takes
            // (SHARE UPDATE EXCLUSIVE) blocks neither reads nor writes. Why these values: docs/adr/0022-database-at-scale.md.
            migrationBuilder.AlterTable(
                name: "stock_movements")
                .Annotation("Npgsql:StorageParameter:autovacuum_vacuum_insert_scale_factor", 0)
                .Annotation("Npgsql:StorageParameter:autovacuum_vacuum_insert_threshold", 100000);

            migrationBuilder.AlterTable(
                name: "products")
                .Annotation("Npgsql:StorageParameter:autovacuum_analyze_scale_factor", "0.02")
                .Annotation("Npgsql:StorageParameter:autovacuum_vacuum_scale_factor", "0.005")
                .Annotation("Npgsql:StorageParameter:autovacuum_vacuum_threshold", 1000);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterTable(
                name: "stock_movements")
                .OldAnnotation("Npgsql:StorageParameter:autovacuum_vacuum_insert_scale_factor", 0)
                .OldAnnotation("Npgsql:StorageParameter:autovacuum_vacuum_insert_threshold", 100000);

            migrationBuilder.AlterTable(
                name: "products")
                .OldAnnotation("Npgsql:StorageParameter:autovacuum_analyze_scale_factor", "0.02")
                .OldAnnotation("Npgsql:StorageParameter:autovacuum_vacuum_scale_factor", "0.005")
                .OldAnnotation("Npgsql:StorageParameter:autovacuum_vacuum_threshold", 1000);
        }
    }
}
