using System.Net;
using System.Net.Http.Json;
using Npgsql;
using ProductManagement.Api.IntegrationTests.Infrastructure;
using ProductManagement.Core.Products.Contracts;
using ProductManagement.Testing;
using static ProductManagement.Testing.ProductApiClient;

namespace ProductManagement.Api.IntegrationTests.Products;

/// <summary>
/// "Sending the same request again must never duplicate anything", whichever replica receives it.
/// </summary>
public class IdempotencyTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task RetryingACreate_ReturnsTheOriginalProduct_AndCreatesOnlyOne()
    {
        var request = TestProducts.CreateRequest(name: "Idempotent frame");

        using var first = await SendAsync(Api, HttpMethod.Post, "api/products", "create-1", request);
        using var retry = await SendAsync(Api, HttpMethod.Post, "api/products", "create-1", request);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.Headers.GetValues("Idempotent-Replayed").Single().ShouldBe("true");
        retry.Headers.Location.ShouldBe(first.Headers.Location);
        (await ReadAsync<ProductResponse>(retry)).Id.ShouldBe((await ReadAsync<ProductResponse>(first)).Id);
        (await CountAsync("SELECT count(*) FROM products WHERE name = 'Idempotent frame'")).ShouldBe(1);
    }

    [Fact]
    public async Task ARetryThatLandsOnAnotherReplica_IsNotAppliedTwice()
    {
        using var replicaB = Environment.CreateClientForAdditionalInstance();
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 10));
        var url = $"api/products/{product.Id}/decrement-stock/3";

        using var onReplicaA = await SendAsync(Api, HttpMethod.Post, url, "order-42");
        using var onReplicaB = await SendAsync(replicaB, HttpMethod.Post, url, "order-42");

        onReplicaA.StatusCode.ShouldBe(HttpStatusCode.OK);
        onReplicaB.StatusCode.ShouldBe(HttpStatusCode.OK);
        onReplicaB.Headers.Contains("Idempotent-Replayed").ShouldBeTrue();
        (await Api.GetProductAsync(product.Id)).StockQuantity.ShouldBe(7);
        (await CountAsync($"SELECT count(*) FROM stock_movements WHERE product_id = {product.Id} AND reason = 'StockRemoved'")).ShouldBe(1);
    }

    [Fact]
    public async Task ARetryWhoseClaimRedisHasLost_IsStillNotAppliedTwice()
    {
        // The case the load test found: the change committed, but the claim in Redis was released or lost (the client
        // went away mid-request, or Redis restarted). The database's unique request key is the backstop.
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 10));
        var url = $"api/products/{product.Id}/decrement-stock/3";

        using var first = await SendAsync(Api, HttpMethod.Post, url, "order-lost-claim");
        await Environment.FlushCacheAsync();
        using var retry = await SendAsync(Api, HttpMethod.Post, url, "order-lost-claim");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.Headers.Contains("Idempotent-Replayed").ShouldBeFalse(); // not from Redis: the database refused it
        (await ReadAsync<ProductResponse>(retry)).StockQuantity.ShouldBe(7);
        (await CountAsync($"SELECT count(*) FROM stock_movements WHERE product_id = {product.Id} AND reason = 'StockRemoved'")).ShouldBe(1);
    }

    [Fact]
    public async Task ACreateRetriedAfterRedisLostTheClaim_ReturnsTheSameProduct()
    {
        var request = TestProducts.CreateRequest(name: "Created once");

        using var first = await SendAsync(Api, HttpMethod.Post, "api/products", "create-lost-claim", request);
        await Environment.FlushCacheAsync();
        using var retry = await SendAsync(Api, HttpMethod.Post, "api/products", "create-lost-claim", request);

        (await ReadAsync<ProductResponse>(retry)).Id.ShouldBe((await ReadAsync<ProductResponse>(first)).Id);
        (await CountAsync("SELECT count(*) FROM products WHERE name = 'Created once'")).ShouldBe(1);
    }

    [Theory]
    [InlineData("api/products/{other}/decrement-stock/3")] // another product
    [InlineData("api/products/{first}/decrement-stock/2")] // another quantity
    [InlineData("api/products/{first}/add-to-stock/3")] // another operation
    public async Task AKeyReusedForAnotherStockChangeAfterRedisForgotIt_IsRejectedAndNothingIsApplied(string reuse)
    {
        // Redis keeps a key for a limited time; the database keeps it forever. Its ledger entry must tell a retry of the
        // same change (answered with success) from a different change under the same key (refused).
        var first = await Api.CreateProductAsync(TestProducts.CreateRequest(name: "First", stockQuantity: 10));
        var other = await Api.CreateProductAsync(TestProducts.CreateRequest(name: "Other", stockQuantity: 10));
        using var original = await SendAsync(Api, HttpMethod.Post, $"api/products/{first.Id}/decrement-stock/3", "order-reused");
        await Environment.FlushCacheAsync();

        var url = reuse.Replace("{first}", $"{first.Id}", StringComparison.Ordinal)
            .Replace("{other}", $"{other.Id}", StringComparison.Ordinal);
        using var reused = await SendAsync(Api, HttpMethod.Post, url, "order-reused");

        original.StatusCode.ShouldBe(HttpStatusCode.OK);
        reused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        reused.Headers.Contains("Idempotent-Replayed").ShouldBeFalse(); // not from Redis: the database refused it
        (await ReadProblemAsync(reused)).ErrorCode.ShouldBe("idempotency_key_reused");
        (await Api.GetProductAsync(first.Id)).StockQuantity.ShouldBe(7);
        (await Api.GetProductAsync(other.Id)).StockQuantity.ShouldBe(10);
        (await CountAsync("SELECT count(*) FROM stock_movements WHERE reason <> 'InitialStock'")).ShouldBe(1);
    }

    [Fact]
    public async Task AStockChangeKeyReusedForACreateAfterRedisForgotIt_IsRejectedAndCreatesNothing()
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 10));
        using var stockChange = await SendAsync(Api, HttpMethod.Post, $"api/products/{product.Id}/decrement-stock/3", "order-then-create");
        await Environment.FlushCacheAsync();

        using var create = await SendAsync(Api, HttpMethod.Post, "api/products", "order-then-create", TestProducts.CreateRequest(name: "Never created"));

        create.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadProblemAsync(create)).ErrorCode.ShouldBe("idempotency_key_reused");
        (await CountAsync("SELECT count(*) FROM products WHERE name = 'Never created'")).ShouldBe(0);
    }

    [Fact]
    public async Task ACreateKeyReusedForAStockChangeAfterRedisForgotIt_IsRejectedAndChangesNothing()
    {
        using var create = await SendAsync(Api, HttpMethod.Post, "api/products", "create-then-order", TestProducts.CreateRequest(stockQuantity: 10));
        var product = await ReadAsync<ProductResponse>(create);
        await Environment.FlushCacheAsync();

        using var stockChange = await SendAsync(Api, HttpMethod.Post, $"api/products/{product.Id}/add-to-stock/10", "create-then-order");

        stockChange.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadProblemAsync(stockChange)).ErrorCode.ShouldBe("idempotency_key_reused");
        (await Api.GetProductAsync(product.Id)).StockQuantity.ShouldBe(10);
    }

    [Fact]
    public async Task TheSameRequestFiredConcurrently_IsExecutedExactlyOnce()
    {
        using var replicaB = Environment.CreateClientForAdditionalInstance();
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 100));
        var url = $"api/products/{product.Id}/decrement-stock/1";

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            SendAsync(i % 2 == 0 ? Api : replicaB, HttpMethod.Post, url, "burst-order")));

        // One executes; the others are told it is in progress (409) or get the replayed result.
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        (await Api.GetProductAsync(product.Id)).StockQuantity.ShouldBe(99);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task RejectedOperations_AreReplayedAsRejected()
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 1));
        var url = $"api/products/{product.Id}/decrement-stock/5";

        using var first = await SendAsync(Api, HttpMethod.Post, url, "too-big");
        (await Api.AddToStockAsync(product.Id, 10)).Dispose(); // now there would be enough
        using var retry = await SendAsync(Api, HttpMethod.Post, url, "too-big");

        // A retry repeats the original outcome; it is not a new attempt.
        first.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        retry.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Api.GetProductAsync(product.Id)).StockQuantity.ShouldBe(11);
    }

    [Fact]
    public async Task ReusingAKeyForADifferentRequest_IsRejected()
    {
        using var first = await SendAsync(Api, HttpMethod.Post, "api/products", "reused", TestProducts.CreateRequest(name: "A"));
        using var second = await SendAsync(Api, HttpMethod.Post, "api/products", "reused", TestProducts.CreateRequest(name: "B"));

        second.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadProblemAsync(second)).ErrorCode.ShouldBe("idempotency_key_reused");
    }

    [Fact]
    public async Task KeysAreScopedPerConsumer()
    {
        using var otherConsumer = Environment.CreateClient(role: null);
        otherConsumer.Http.DefaultRequestHeaders.Authorization =
            new("Bearer", TestTokens.Create(TestTokens.Manager, subject: "another-manager"));

        using var mine = await SendAsync(Api, HttpMethod.Post, "api/products", "shared-key", TestProducts.CreateRequest(name: "Mine"));
        using var theirs = await SendAsync(otherConsumer, HttpMethod.Post, "api/products", "shared-key", TestProducts.CreateRequest(name: "Theirs"));

        theirs.StatusCode.ShouldBe(HttpStatusCode.Created);
        theirs.Headers.Contains("Idempotent-Replayed").ShouldBeFalse();
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("emoji-🙂")]
    public async Task InvalidKeys_AreRejected(string key)
    {
        using var response = await SendAsync(Api, HttpMethod.Post, "api/products", key, TestProducts.CreateRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).ErrorCode.ShouldBe("idempotency_key_invalid");
    }

    [Fact]
    public async Task WithoutTheStore_KeyedRequestsAreRefusedRatherThanRiskingDuplicates()
    {
        using var withoutRedis = Environment.CreateClientWithUnreachableCache();

        using var keyed = await SendAsync(withoutRedis, HttpMethod.Post, "api/products", "k1", TestProducts.CreateRequest());
        using var unkeyed = await withoutRedis.CreateAsync(TestProducts.CreateRequest());

        keyed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ReadProblemAsync(keyed)).ErrorCode.ShouldBe("idempotency_unavailable");
        unkeyed.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private static Task<HttpResponseMessage> SendAsync(
        ProductApiClient client,
        HttpMethod method,
        string url,
        string idempotencyKey,
        object? body = null)
    {
        var message = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (body is not null)
        {
            message.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return client.Http.SendAsync(message);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Environment.DatabaseConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
    }
}
