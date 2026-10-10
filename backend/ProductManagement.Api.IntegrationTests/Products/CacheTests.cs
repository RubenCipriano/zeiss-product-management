using System.Net;
using Npgsql;
using ProductManagement.Api.IntegrationTests.Infrastructure;
using ProductManagement.Testing;

namespace ProductManagement.Api.IntegrationTests.Products;

/// <summary>
/// The Redis read cache: reads are served from it, writes through the API invalidate what they affect for all
/// replicas, and an unavailable cache never breaks the API. (Every other test in this assembly also runs with the cache on,
/// so read-after-write correctness is covered across the whole suite.)
/// </summary>
public class CacheTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Reads_AreServedFromTheCache_UntilAWriteThroughTheApiInvalidatesIt()
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(name: "Original name"));
        var other = await Api.CreateProductAsync(TestProducts.CreateRequest(name: "Other"));
        (await Api.GetProductAsync(product.Id)).Name.ShouldBe("Original name"); // warms the cache

        // Change the row behind the API's back: no invalidation happens.
        await RenameDirectlyInDatabaseAsync(product.Id, "Changed in the database");
        (await Api.GetProductAsync(product.Id)).Name.ShouldBe("Original name"); // still served from the cache

        // A write to a different product leaves this product's entry alone: invalidation is per product...
        (await Api.AddToStockAsync(other.Id, 1)).Dispose();
        (await Api.GetProductAsync(product.Id)).Name.ShouldBe("Original name");

        // ...and a write to this product, even a stock change, invalidates it.
        (await Api.AddToStockAsync(product.Id, 1)).Dispose();
        (await Api.GetProductAsync(product.Id)).Name.ShouldBe("Changed in the database");
    }

    [Fact]
    public async Task AWriteOnOneReplica_IsVisibleImmediatelyOnAnother()
    {
        using var replicaB = Environment.CreateClientForAdditionalInstance();
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 10));

        // Both replicas read (and cache) the product and the list.
        (await replicaB.GetProductAsync(product.Id)).StockQuantity.ShouldBe(10);
        using (var list = await replicaB.ListAsync())
        {
            (await ProductApiClient.ReadPageAsync(list)).Items.Single().StockQuantity.ShouldBe(10);
        }

        // Replica A changes the stock...
        (await Api.DecrementStockAsync(product.Id, 4)).Dispose();

        // ...and replica B sees it straight away, both for the item and for the list.
        (await replicaB.GetProductAsync(product.Id)).StockQuantity.ShouldBe(6);
        using var listAfter = await replicaB.ListAsync();
        (await ProductApiClient.ReadPageAsync(listAfter)).Items.Single().StockQuantity.ShouldBe(6);
    }

    [Fact]
    public async Task StockChanges_KeepListsCached_WhileTheyStillShowTheCurrentStock()
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(name: "Listed", stockQuantity: 10));
        using (var warm = await Api.ListAsync())
        {
            (await ProductApiClient.ReadPageAsync(warm)).TotalCount.ShouldBe(1);
        }

        // A product appears behind the API's back, then the stock of the listed one changes.
        await ExecuteSqlAsync(
            "INSERT INTO products (id, name, price, stock_quantity, category_id, created_at, updated_at) " +
            "VALUES (999998, 'Inserted directly', 1, 1, 1, now(), now())");
        (await Api.DecrementStockAsync(product.Id, 4)).Dispose();

        // Which products the list holds is still cached (a stock change cannot alter it), but the row is current.
        using (var afterStockChange = await Api.ListAsync())
        {
            var page = await ProductApiClient.ReadPageAsync(afterStockChange);
            page.TotalCount.ShouldBe(1);
            page.Items.Single().StockQuantity.ShouldBe(6);
        }

        // A change to the catalogue itself (an edit) refreshes the list.
        var current = await Api.GetProductAsync(product.Id);
        (await Api.UpdateAsync(product.Id, TestProducts.UpdateRequest(current, name: "Listed, renamed"))).Dispose();
        using var afterEdit = await Api.ListAsync();
        (await ProductApiClient.ReadPageAsync(afterEdit)).TotalCount.ShouldBe(2);
    }

    [Fact]
    public async Task NotFound_IsNotCached()
    {
        const int id = 999_999;
        using (var missing = await Api.GetAsync(id))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // The product appears without any API write (so without any invalidation)...
        await ExecuteSqlAsync(
            "INSERT INTO products (id, name, price, stock_quantity, category_id, created_at, updated_at) " +
            "VALUES (@id, 'Inserted directly', 1, 1, 1, now(), now())",
            ("id", id));

        // ...and is found at once, because the earlier 404 was not stored in the cache.
        (await Api.GetAsync(id)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheApi_KeepsWorking_WhenTheCacheIsUnreachable()
    {
        using var withoutCache = Environment.CreateClientWithUnreachableCache();

        var created = await withoutCache.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 3));
        using var decrement = await withoutCache.DecrementStockAsync(created.Id, 1);
        var read = await withoutCache.GetProductAsync(created.Id);

        decrement.StatusCode.ShouldBe(HttpStatusCode.OK);
        read.StockQuantity.ShouldBe(2);

        // Readiness stays green (the cache is reported as degraded, not unhealthy), so the replica keeps serving.
        using var health = await withoutCache.Http.GetAsync(new Uri("health/ready", UriKind.Relative), CancellationToken);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await health.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("Degraded");
    }

    private Task RenameDirectlyInDatabaseAsync(int productId, string name) =>
        ExecuteSqlAsync("UPDATE products SET name = @name WHERE id = @id", ("name", name), ("id", productId));

    private async Task ExecuteSqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Environment.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        (await command.ExecuteNonQueryAsync(CancellationToken)).ShouldBe(1);
    }
}
