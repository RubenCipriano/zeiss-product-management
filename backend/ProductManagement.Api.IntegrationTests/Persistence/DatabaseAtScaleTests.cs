using Npgsql;
using ProductManagement.Api.IntegrationTests.Infrastructure;

namespace ProductManagement.Api.IntegrationTests.Persistence;

/// <summary>
/// The physical design the large-catalogue measurements rely on (docs/adr/0022-database-at-scale.md), as the
/// migrations create it: each index serves a query, and the hot tables are vacuumed early enough for index-only scans.
/// </summary>
public class DatabaseAtScaleTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task EveryIndex_IsTheOneTheQueriesNeed_AndNoneIsMissingOrExtra()
    {
        var indexes = await QueryAsync(
            """
            SELECT tablename || ': ' || indexdef
            FROM pg_indexes
            WHERE schemaname = 'public' AND tablename IN ('products', 'stock_movements', 'categories')
            ORDER BY tablename, indexname
            """);

        indexes.ShouldBe(
        [
            "categories: CREATE UNIQUE INDEX ix_categories_name ON public.categories USING btree (name)",
            "categories: CREATE UNIQUE INDEX pk_categories ON public.categories USING btree (id)",
            "products: CREATE INDEX ix_products_category_id ON public.products USING btree (category_id)",
            "products: CREATE INDEX ix_products_name ON public.products USING gin (name gin_trgm_ops)",
            "products: CREATE INDEX ix_products_name_id ON public.products USING btree (name, id)",
            "products: CREATE INDEX ix_products_stock_quantity_name_id ON public.products USING btree (stock_quantity, name, id)",
            "products: CREATE UNIQUE INDEX pk_products ON public.products USING btree (id)",
            "stock_movements: CREATE INDEX ix_stock_movements_product_id_occurred_at_id ON public.stock_movements USING btree (product_id, occurred_at DESC, id DESC)",
            "stock_movements: CREATE UNIQUE INDEX ix_stock_movements_request_key ON public.stock_movements USING btree (request_key) WHERE (request_key IS NOT NULL)",
            "stock_movements: CREATE UNIQUE INDEX pk_stock_movements ON public.stock_movements USING btree (id)",
        ]);
    }

    [Fact]
    public async Task HotTables_AreVacuumedEarly()
    {
        var options = await QueryAsync(
            """
            SELECT relname || ': ' || array_to_string(array(SELECT unnest(reloptions) ORDER BY 1), ', ')
            FROM pg_class
            WHERE relname IN ('products', 'stock_movements') AND relkind = 'r'
            ORDER BY relname
            """);

        options.ShouldBe(
        [
            "products: autovacuum_analyze_scale_factor=0.02, autovacuum_vacuum_scale_factor=0.005, autovacuum_vacuum_threshold=1000",
            "stock_movements: autovacuum_vacuum_insert_scale_factor=0, autovacuum_vacuum_insert_threshold=100000",
        ]);
    }

    private async Task<List<string>> QueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Environment.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
