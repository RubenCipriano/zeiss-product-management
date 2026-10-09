using System.Net;
using ProductManagement.Api.IntegrationTests.Infrastructure;

namespace ProductManagement.Api.IntegrationTests;

public sealed class ApiBehaviourTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Theory]
    [InlineData("health/live")]
    [InlineData("health/ready")]
    public async Task HealthEndpoints_ReportHealthy(string path)
    {
        using var response = await Api.Http.GetAsync(new Uri(path, UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldBe("Healthy");
    }

    [Fact]
    public async Task UnknownRoutes_ReturnProblemDetails()
    {
        using var response = await Api.Http.GetAsync(new Uri("api/does-not-exist", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task OpenApiDocument_DescribesAllProductEndpoints()
    {
        var document = await Api.Http.GetStringAsync(new Uri("openapi/v1.json", UriKind.Relative), CancellationToken);

        document.ShouldContain("/api/products/{id}/decrement-stock/{quantity}");
        document.ShouldContain("/api/products/{id}/add-to-stock/{quantity}");
        document.ShouldContain("/api/products/search");
        document.ShouldContain("/api/products/stock-level");
    }
}
