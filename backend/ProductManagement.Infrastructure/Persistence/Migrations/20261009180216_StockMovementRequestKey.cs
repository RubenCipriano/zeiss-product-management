using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProductManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StockMovementRequestKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "request_key",
                table: "stock_movements",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            // A nullable column without a default is a metadata-only change, instant even on millions of rows. The
            // index is built CONCURRENTLY so the ledger keeps accepting writes meanwhile (outside a transaction;
            // IF NOT EXISTS makes a re-run after an interruption safe).
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ix_stock_movements_request_key " +
                "ON stock_movements (request_key) WHERE request_key IS NOT NULL;",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_stock_movements_request_key",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "request_key",
                table: "stock_movements");
        }
    }
}
