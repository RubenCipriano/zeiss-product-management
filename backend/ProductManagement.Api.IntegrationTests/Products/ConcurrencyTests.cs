using System.Net;
using ProductManagement.Api.IntegrationTests.Infrastructure;
using ProductManagement.Testing;
using static ProductManagement.Testing.ProductApiClient;

namespace ProductManagement.Api.IntegrationTests.Products;

/// <summary>
/// The guarantees that only matter under concurrent load, verified with real parallel HTTP requests against a
/// real PostgreSQL instance.
/// </summary>
public class ConcurrencyTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task ConcurrentDecrements_NeverOversell()
    {
        const int initialStock = 25;
        const int concurrentOrders = 60;
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: initialStock));

        var statuses = await Task.WhenAll(Enumerable.Range(0, concurrentOrders).Select(async _ =>
        {
            using var response = await Api.DecrementStockAsync(product.Id, 1);
            return response.StatusCode;
        }));

        statuses.Count(status => status == HttpStatusCode.OK).ShouldBe(initialStock);
        statuses.Count(status => status == HttpStatusCode.Conflict).ShouldBe(concurrentOrders - initialStock);
        (await Api.GetProductAsync(product.Id)).StockQuantity.ShouldBe(0);
    }

    [Fact]
    public async Task ConcurrentIncrementsAndDecrements_LoseNoUpdates()
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 100));

        var requests = Enumerable.Range(0, 100).Select(async index =>
        {
            using var response = index % 2 == 0
                ? await Api.AddToStockAsync(product.Id, 3)
                : await Api.DecrementStockAsync(product.Id, 2);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        });
        await Task.WhenAll(requests);

        // 50 x (+3) and 50 x (-2) => +50, regardless of interleaving.
        (await Api.GetProductAsync(product.Id)).StockQuantity.ShouldBe(150);
    }

    [Fact]
    public async Task IdGeneration_AcrossMultipleApiInstances_NeverProducesDuplicates()
    {
        // Two independent hosts (separate DI containers and connection pools) sharing one database,
        // as in a horizontally scaled deployment.
        using var instanceB = Environment.CreateClientForAdditionalInstance();
        var clients = new[] { Api, instanceB };
        const int productsPerInstance = 75;

        var ids = await Task.WhenAll(Enumerable.Range(0, productsPerInstance * clients.Length).Select(async index =>
        {
            var product = await clients[index % clients.Length]
                .CreateProductAsync(TestProducts.CreateRequest(name: $"Product {index}"));
            return product.Id;
        }));

        ids.Distinct().Count().ShouldBe(ids.Length);
        ids.ShouldAllBe(id => id >= 100_000 && id <= 999_999);
    }

    [Fact]
    public async Task Update_WithAnETagMadeStaleByAnotherEdit_IsRejectedAndKeepsTheOtherEdit()
    {
        var original = await Api.CreateProductAsync(TestProducts.CreateRequest(name: "Original"));
        var etag = $"\"{original.Version}\"";

        using var first = await Api.UpdateAsync(original.Id, TestProducts.UpdateRequest(original, name: "First edit"), etag);
        using var second = await Api.UpdateAsync(original.Id, TestProducts.UpdateRequest(original, name: "Second edit"), etag);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ReadProblemAsync(second)).ErrorCode.ShouldBe("product.version_mismatch");
        (await Api.GetProductAsync(original.Id)).Name.ShouldBe("First edit");
    }

    [Fact]
    public async Task StockChange_InvalidatesETagsHeldByEditors()
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 10));
        (await Api.DecrementStockAsync(product.Id, 1)).Dispose();

        // The editor still holds the version from before the sale; saving would silently restore stock to 10.
        using var response = await Api.UpdateAsync(
            product.Id,
            TestProducts.UpdateRequest(product, name: "Edited"),
            ifMatch: $"\"{product.Version}\"");

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await Api.GetProductAsync(product.Id)).StockQuantity.ShouldBe(9);
    }
}
