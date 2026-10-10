using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Options;
using Npgsql;
using ProductManagement.Api.IntegrationTests.Infrastructure;
using ProductManagement.Testing;

namespace ProductManagement.Api.IntegrationTests;

public class ApiBehaviourTests(ApiFixture fixture) : ApiTestBase(fixture)
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
    public async Task Readiness_StaysHealthy_WhileEveryRequestConnectionIsBusy()
    {
        // A busy replica is not a broken one: readiness must not wait for the request pool, or every replica leaves the
        // load balancer exactly when the load is highest.
        using var instance = Environment.CreateClientForInstanceWith(new Dictionary<string, string>
        {
            ["ConnectionStrings:ProductManagement"] = Environment.DatabaseConnectionString + ";Maximum Pool Size=1;Timeout=10",
        });
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 10));
        await using var blocker = new NpgsqlConnection(Environment.DatabaseConnectionString);
        await blocker.OpenAsync(CancellationToken);
        await using var transaction = await blocker.BeginTransactionAsync(CancellationToken);
        await using (var lockRow = new NpgsqlCommand($"SELECT 1 FROM products WHERE id = {product.Id} FOR UPDATE", blocker, transaction))
        {
            await lockRow.ExecuteScalarAsync(CancellationToken);
        }

        // Waits for the row lock, holding the instance's only request connection.
        var blocked = instance.DecrementStockAsync(product.Id, 1);
        await WaitUntilAsync(async () => await CountWaitingLocksAsync() > 0);
        var stopwatch = Stopwatch.StartNew();
        using var ready = await instance.Http.GetAsync(new Uri("health/ready", UriKind.Relative), CancellationToken);
        stopwatch.Stop();
        await transaction.CommitAsync(CancellationToken);

        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        using var decremented = await blocked;
        decremented.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnknownRoutes_ReturnProblemDetails()
    {
        using var response = await Api.Http.GetAsync(new Uri("api/does-not-exist", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await ProductApiClient.ReadProblemAsync(response)).ErrorCode.ShouldBe("http_404");
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

    [Theory]
    [InlineData("RateLimiting:WritesPerSecond", "0")]
    [InlineData("SignInThrottle:MaxFailures", "0")]
    [InlineData("Idempotency:InProgressTimeout", "00:00:00")]
    [InlineData("Authentication:SigningKey", "")]
    public void AnInstanceWithAnInvalidSetting_RefusesToStart(string key, string value)
    {
        var exception = Should.Throw<OptionsValidationException>(() =>
            Environment.CreateClientForInstanceWith(new Dictionary<string, string> { [key] = value }));

        exception.Message.ShouldContain(key.Split(':')[1]);
    }

    private async Task<long> CountWaitingLocksAsync()
    {
        await using var connection = new NpgsqlConnection(Environment.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE NOT granted", connection);
        return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; !await condition(); attempt++)
        {
            attempt.ShouldBeLessThan(100, "The condition did not become true within 5 s.");
            await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken);
        }
    }
}
