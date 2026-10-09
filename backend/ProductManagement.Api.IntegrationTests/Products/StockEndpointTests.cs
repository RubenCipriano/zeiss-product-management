using System.Net;
using ProductManagement.Api.IntegrationTests.Infrastructure;
using ProductManagement.Application.Products.Contracts;
using ProductManagement.Domain.Products;
using ProductManagement.Testing;
using static ProductManagement.Testing.ProductApiClient;

namespace ProductManagement.Api.IntegrationTests.Products;

public sealed class StockEndpointTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task DecrementStock_ReturnsOkWithTheUpdatedProduct()
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 10));

        using var response = await Api.DecrementStockAsync(product.Id, 4);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await ReadAsync<ProductResponse>(response);
        updated.StockQuantity.ShouldBe(6);
        updated.Version.ShouldNotBe(product.Version);
        updated.UpdatedAt.ShouldBeGreaterThan(product.UpdatedAt);
    }

    [Fact]
    public async Task DecrementStock_DownToExactlyZero_IsAllowed()
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 3));

        using var response = await Api.DecrementStockAsync(product.Id, 3);

        (await ReadAsync<ProductResponse>(response)).StockQuantity.ShouldBe(0);
    }

    [Fact]
    public async Task DecrementStock_BeyondAvailableStock_IsRejectedAndLeavesStockUnchanged()
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 3));

        using var response = await Api.DecrementStockAsync(product.Id, 4);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ReadProblemAsync(response);
        problem.ErrorCode.ShouldBe("product.insufficient_stock");
        problem.Requested.ShouldBe(4);
        problem.Available.ShouldBe(3);
        (await Api.GetProductAsync(product.Id)).StockQuantity.ShouldBe(3);
    }

    [Fact]
    public async Task AddToStock_ReturnsOkWithTheUpdatedProduct()
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 10));

        using var response = await Api.AddToStockAsync(product.Id, 15);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<ProductResponse>(response)).StockQuantity.ShouldBe(25);
    }

    [Fact]
    public async Task AddToStock_BeyondTheMaximum_IsRejected()
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: ProductRules.MaxStockQuantity - 1));

        using var response = await Api.AddToStockAsync(product.Id, 2);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response)).ErrorCode.ShouldBe("product.stock_limit_exceeded");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ProductRules.MaxStockQuantity + 1)]
    public async Task StockEndpoints_RejectInvalidQuantities(int quantity)
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest());

        using var decrement = await Api.DecrementStockAsync(product.Id, quantity);
        using var add = await Api.AddToStockAsync(product.Id, quantity);

        decrement.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        add.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(decrement)).Errors!.ShouldContainKey("quantity");
    }

    [Fact]
    public async Task StockEndpoints_WithNonNumericQuantity_ReturnBadRequest()
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest());

        using var response = await Api.Http.PostAsync(
            new Uri($"api/products/{product.Id}/decrement-stock/abc", UriKind.Relative),
            content: null,
            CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task StockEndpoints_ForUnknownProduct_ReturnNotFound()
    {
        using var decrement = await Api.DecrementStockAsync(999_999, 1);
        using var add = await Api.AddToStockAsync(999_999, 1);

        decrement.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        add.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
