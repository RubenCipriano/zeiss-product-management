using System.Net;
using ModelContextProtocol;
using ProductManagement.Mcp.Api;
using ProductManagement.Mcp.Tools;

namespace ProductManagement.Mcp.UnitTests;

public sealed class ToolTests : IDisposable
{
    private const string EmptyPage = """{"items":[],"page":1,"pageSize":100,"totalCount":0,"totalPages":0}""";

    private readonly StubHttpHandler _handler = new();
    private readonly ProductApiClient _api;

    public ToolTests() =>
        _api = new ProductApiClient(new HttpClient(_handler) { BaseAddress = new Uri("http://gateway/") });

    public void Dispose()
    {
        _handler.Dispose();
    }

    [Fact]
    public async Task ListLowStock_AsksForStockBetweenZeroAndTheThreshold()
    {
        _handler.Respond(HttpStatusCode.OK, EmptyPage);

        await new ProductTools(_api).ListLowStock(TestContext.Current.CancellationToken);

        _handler.Requests.Single().RequestUri!.PathAndQuery
            .ShouldBe("/api/products/stock-level?page=1&pageSize=100&min=0&max=10");
    }

    [Fact]
    public async Task ApiRejections_AreReportedToTheAssistantWithTheirErrorCode()
    {
        _handler.Respond(
            HttpStatusCode.Forbidden,
            """{"status":403,"title":"Forbidden","errorCode":"http_403"}""");

        var exception = await Should.ThrowAsync<McpException>(() =>
            new StockTools(_api).RemoveStock(100004, 1, TestContext.Current.CancellationToken));

        exception.Message.ShouldBe("Forbidden [http_403]");
    }
}
