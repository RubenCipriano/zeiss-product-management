using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ProductManagement.Api.IntegrationTests.Infrastructure;
using ProductManagement.Api.Security;
using ProductManagement.Testing;
using static ProductManagement.Testing.ProductApiClient;

namespace ProductManagement.Api.IntegrationTests.Security;

/// <summary>The API's own token endpoint: registered consumers exchange credentials for a signed JWT.</summary>
public sealed class TokenEndpointTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task ValidCredentials_ReturnABearerTokenThatTheApiAccepts()
    {
        using var anonymous = Environment.CreateClient(role: null);

        using var response = await RequestTokenAsync(anonymous, TestTokens.ConsumerId, TestTokens.ConsumerSecret);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = await ReadAsync<TokenResponse>(response);
        token.TokenType.ShouldBe("Bearer");
        token.ExpiresIn.ShouldBe(3600);

        anonymous.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var products = await anonymous.ListAsync();
        products.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheTokenCarriesTheConsumersRoles()
    {
        using var anonymous = Environment.CreateClient(role: null);
        using var response = await RequestTokenAsync(anonymous, TestTokens.ConsumerId, TestTokens.ConsumerSecret);
        var token = await ReadAsync<TokenResponse>(response);
        anonymous.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // The test consumer is a viewer: reading works, creating is forbidden.
        using var create = await anonymous.CreateAsync(TestProducts.CreateRequest());
        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(TestTokens.ConsumerId, "wrong-secret")]
    [InlineData("unknown-consumer", TestTokens.ConsumerSecret)]
    public async Task InvalidCredentials_AreRejectedWithoutRevealingWhichPartIsWrong(string clientId, string secret)
    {
        using var anonymous = Environment.CreateClient(role: null);

        using var response = await RequestTokenAsync(anonymous, clientId, secret);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var problem = await ReadProblemAsync(response);
        problem.ErrorCode.ShouldBe("invalid_client");
        problem.Detail.ShouldBe("Invalid client credentials.");
    }

    [Fact]
    public async Task MissingCredentials_AreAValidationError()
    {
        using var anonymous = Environment.CreateClient(role: null);

        using var response = await anonymous.Http.PostAsJsonAsync("api/auth/token", new { clientId = "x" }, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RepeatedAttempts_AreThrottledToStopSecretGuessing()
    {
        using var client = Environment.CreateClientForInstanceWith(
            new Dictionary<string, string> { ["RateLimiting:TokenRequestsPerMinute"] = "3" },
            role: null);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var response = await RequestTokenAsync(client, TestTokens.ConsumerId, "guess-" + i);
            statuses.Add(response.StatusCode);
        }

        statuses.Take(3).ShouldAllBe(status => status == HttpStatusCode.Unauthorized);
        statuses.Skip(3).ShouldAllBe(status => status == HttpStatusCode.TooManyRequests);
    }

    private static Task<HttpResponseMessage> RequestTokenAsync(ProductApiClient client, string clientId, string secret) =>
        client.Http.PostAsJsonAsync("api/auth/token", new { clientId, clientSecret = secret }, CancellationToken);
}
