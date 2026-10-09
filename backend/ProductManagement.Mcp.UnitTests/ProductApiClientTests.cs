using System.Net;
using ProductManagement.Mcp.Api;

namespace ProductManagement.Mcp.UnitTests;

public sealed class ProductApiClientTests : IDisposable
{
    private const string ProductJson =
        """{"id":100004,"name":"Office Lens","description":null,"price":189.0,"stockQuantity":5,"categoryId":1,"categoryName":"Lenses","updatedAt":"2026-10-09T10:00:00+00:00","version":12}""";

    private const string EmptyPage = """{"items":[],"page":1,"pageSize":20,"totalCount":0,"totalPages":0}""";

    private readonly StubHttpHandler _handler = new();
    private readonly ProductApiClient _client;

    public ProductApiClientTests() =>
        _client = new ProductApiClient(new HttpClient(_handler) { BaseAddress = new Uri("http://gateway/") });

    public void Dispose()
    {
        _handler.Dispose();
    }

    [Fact]
    public async Task Search_EscapesTheTerm()
    {
        _handler.Respond(HttpStatusCode.OK, EmptyPage);

        await _client.SearchAsync("50% off & more", 1, 20, TestContext.Current.CancellationToken);

        _handler.Requests.Single().RequestUri!.PathAndQuery
            .ShouldBe("/api/products/search?name=50%25%20off%20%26%20more&page=1&pageSize=20");
    }

    [Fact]
    public async Task StockLevel_OmitsOpenBounds()
    {
        _handler.Respond(HttpStatusCode.OK, EmptyPage);

        await _client.ByStockLevelAsync(null, 10, 1, 20, TestContext.Current.CancellationToken);

        _handler.Requests.Single().RequestUri!.PathAndQuery.ShouldBe("/api/products/stock-level?page=1&pageSize=20&max=10");
    }

    [Theory]
    [InlineData(true, "/api/products/100004/add-to-stock/3")]
    [InlineData(false, "/api/products/100004/decrement-stock/3")]
    public async Task AdjustStock_PostsToTheMatchingEndpoint(bool add, string expectedPath)
    {
        _handler.Respond(HttpStatusCode.OK, ProductJson);

        var product = await _client.AdjustStockAsync(100004, 3, add, TestContext.Current.CancellationToken);

        _handler.Requests.Single().Method.ShouldBe(HttpMethod.Post);
        _handler.Requests.Single().RequestUri!.AbsolutePath.ShouldBe(expectedPath);
        product.StockQuantity.ShouldBe(5);
    }

    [Fact]
    public async Task ProblemResponses_BecomeExceptionsWithTheApiMessageAndCode()
    {
        _handler.Respond(
            HttpStatusCode.Conflict,
            """{"status":409,"title":"Conflict","detail":"Cannot remove 10 unit(s) from product 100004: only 5 available.","errorCode":"product.insufficient_stock"}""");

        var exception = await Should.ThrowAsync<ProductApiException>(() =>
            _client.AdjustStockAsync(100004, 10, add: false, TestContext.Current.CancellationToken));

        exception.Status.ShouldBe(409);
        exception.ErrorCode.ShouldBe("product.insufficient_stock");
        exception.Message.ShouldContain("only 5 available");
    }

    [Fact]
    public async Task NonProblemErrors_StillProduceAReadableMessage()
    {
        _handler.Respond(HttpStatusCode.BadGateway, "<html>bad gateway</html>");

        var exception = await Should.ThrowAsync<ProductApiException>(() =>
            _client.GetAsync(1, TestContext.Current.CancellationToken));

        exception.Message.ShouldBe("The API answered 502.");
    }
}
