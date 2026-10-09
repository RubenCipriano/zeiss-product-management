using System.Net;
using ProductManagement.Api.IntegrationTests.Infrastructure;
using ProductManagement.Testing;
using static ProductManagement.Testing.ProductApiClient;

namespace ProductManagement.Api.IntegrationTests.Security;

public sealed class RateLimitingTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private static readonly Dictionary<string, string> LowLimits = new()
    {
        ["RateLimiting:RequestsPerSecond"] = "1",
        ["RateLimiting:RequestBurst"] = "5",
        ["RateLimiting:WritesPerSecond"] = "1",
        ["RateLimiting:WriteBurst"] = "2",
    };

    [Fact]
    public async Task Callers_ExceedingTheirLimit_Get429WithRetryAfter()
    {
        using var client = Environment.CreateClientForInstanceWith(LowLimits);

        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 10; i++)
        {
            responses.Add(await client.ListAsync());
        }

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBeInRange(5, 6);
        var rejected = responses.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.ShouldNotBeNull();
        (await ReadProblemAsync(rejected)).ErrorCode.ShouldBe("rate_limited");
        responses.ForEach(r => r.Dispose());
    }

    [Fact]
    public async Task Writes_HaveAStricterLimitThanReads()
    {
        using var client = Environment.CreateClientForInstanceWith(LowLimits);
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 100));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await client.DecrementStockAsync(product.Id, 1);
            statuses.Add(response.StatusCode);
        }

        statuses.Take(2).ShouldAllBe(status => status == HttpStatusCode.OK);
        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Limits_ArePerUser()
    {
        var settings = new Dictionary<string, string>(LowLimits) { ["RateLimiting:RequestBurst"] = "2" };
        using var viewer = Environment.CreateClientForInstanceWith(settings, TestTokens.Viewer);
        using var clerk = Environment.CreateClientForInstanceWith(settings, TestTokens.StockClerk);

        for (var i = 0; i < 5; i++)
        {
            (await viewer.ListAsync()).Dispose();
        }

        // The viewer has used up their bucket; another user is unaffected.
        using var viewerAgain = await viewer.ListAsync();
        using var clerkRequest = await clerk.ListAsync();
        viewerAgain.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        clerkRequest.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthProbes_AreNeverRateLimited()
    {
        using var client = Environment.CreateClientForInstanceWith(LowLimits, role: null);

        for (var i = 0; i < 20; i++)
        {
            using var response = await client.Http.GetAsync(new Uri("health/live", UriKind.Relative), CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }
}
