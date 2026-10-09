using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
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
    public async Task TheSigningKeysArePublished_PublicPartOnly_AndMatchTheTokens()
    {
        using var anonymous = Environment.CreateClient(role: null);
        using var tokenResponse = await RequestTokenAsync(anonymous, TestTokens.ConsumerId, TestTokens.ConsumerSecret);
        var token = new JsonWebToken((await ReadAsync<TokenResponse>(tokenResponse)).AccessToken);

        using var response = await anonymous.Http.GetAsync(
            new Uri("/.well-known/jwks.json", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var jwks = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken));
        var key = jwks.RootElement.GetProperty("keys").EnumerateArray().Single();
        key.GetProperty("kty").GetString().ShouldBe("EC");
        key.GetProperty("crv").GetString().ShouldBe("P-256");
        key.GetProperty("alg").GetString().ShouldBe("ES256");
        key.TryGetProperty("d", out _).ShouldBeFalse(); // never the private key
        token.Alg.ShouldBe("ES256");
        token.Kid.ShouldBe(key.GetProperty("kid").GetString());
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

    [Fact]
    public async Task FailedAttempts_AreCountedAcrossReplicas_AndBlockTheCallerEvenWithTheRightSecret()
    {
        var settings = new Dictionary<string, string> { ["SignInThrottle:MaxFailures"] = "3" };
        using var replicaA = Environment.CreateClientForInstanceWith(settings, role: null);
        using var replicaB = Environment.CreateClientForInstanceWith(settings, role: null);

        (await RequestTokenAsync(replicaA, TestTokens.ConsumerId, "guess-1")).Dispose();
        (await RequestTokenAsync(replicaA, TestTokens.ConsumerId, "guess-2")).Dispose();
        (await RequestTokenAsync(replicaB, TestTokens.ConsumerId, "guess-3")).Dispose();

        // Three failures in total, spread over two replicas: the caller is now blocked everywhere.
        using var blocked = await RequestTokenAsync(replicaB, TestTokens.ConsumerId, TestTokens.ConsumerSecret);

        blocked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        blocked.Headers.RetryAfter.ShouldNotBeNull();
        (await ReadProblemAsync(blocked)).ErrorCode.ShouldBe("too_many_failed_attempts");
    }

    private static Task<HttpResponseMessage> RequestTokenAsync(ProductApiClient client, string clientId, string secret) =>
        client.Http.PostAsJsonAsync("api/auth/token", new { clientId, clientSecret = secret }, CancellationToken);
}
