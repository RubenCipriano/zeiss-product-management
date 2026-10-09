using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProductManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StockLevelIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CONCURRENTLY: build the index without blocking writes, which matters on a large, busy table. It cannot
            // run inside a transaction, hence suppressTransaction. The new index is created before the old one is
            // dropped, so the stock-level query is never left without one. Both statements are idempotent
            // (IF [NOT] EXISTS), so if the migration is interrupted between them it can simply be run again.
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_products_stock_quantity_name_id " +
                "ON products (stock_quantity, name, id);",
                suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_products_stock_quantity;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_products_stock_quantity_name_id",
                table: "products");

            migrationBuilder.CreateIndex(
                name: "ix_products_stock_quantity",
                table: "products",
                column: "stock_quantity");
        }
    }
}
