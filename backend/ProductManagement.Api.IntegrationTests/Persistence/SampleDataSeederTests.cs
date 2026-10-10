using System.Globalization;
using Npgsql;
using ProductManagement.Api.IntegrationTests.Infrastructure;
using ProductManagement.Testing;

namespace ProductManagement.Api.IntegrationTests.Persistence;

/// <summary>
/// The optional sample catalogue (<c>Database:SeedSampleData</c>), seeded after the migrations by an instance that
/// applies them: an empty database gets every sample product with its ledger; a database with products is left alone.
/// </summary>
public class SampleDataSeederTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private static readonly Dictionary<string, string> SeedingInstance = new()
    {
        ["Database:ApplyMigrationsOnStartup"] = bool.TrueString,
        ["Database:SeedSampleData"] = bool.TrueString,
    };

    [Fact]
    public async Task AnEmptyDatabase_IsSeededOnce_WithEveryProductsInitialStockInTheLedger()
    {
        StartSeedingInstance();
        var seeded = await QueryAsync("SELECT string_agg(id::text, ',' ORDER BY id) FROM products");
        StartSeedingInstance();

        (await CountAsync("SELECT count(*) FROM products")).ShouldBeGreaterThan(0);
        (await CountAsync("SELECT count(*) FROM stock_movements")).ShouldBe(await CountAsync("SELECT count(*) FROM products"));
        (await CountAsync(
            """
            SELECT count(*) FROM products p
            WHERE NOT EXISTS (
                SELECT 1 FROM stock_movements m
                WHERE m.product_id = p.id AND m.reason = 'InitialStock' AND m.performed_by = 'seed'
                  AND m.quantity_change = p.stock_quantity AND m.stock_after = p.stock_quantity
                  AND m.occurred_at = p.created_at)
            """)).ShouldBe(0);
        (await QueryAsync("SELECT string_agg(id::text, ',' ORDER BY id) FROM products")).ShouldBe(seeded);
    }

    [Fact]
    public async Task ADatabaseThatHasProducts_IsLeftAlone()
    {
        await Api.CreateProductAsync(TestProducts.CreateRequest(name: "Real product"));

        StartSeedingInstance();

        (await CountAsync("SELECT count(*) FROM products")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM stock_movements")).ShouldBe(1);
    }

    // Creating the client boots the instance, which applies the migrations and then seeds.
    private void StartSeedingInstance() => Environment.CreateClientForInstanceWith(SeedingInstance).Dispose();

    private async Task<long> CountAsync(string sql) => long.Parse(await QueryAsync(sql), CultureInfo.InvariantCulture);

    private async Task<string> QueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Environment.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken), CultureInfo.InvariantCulture)!;
    }
}
