using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ProductManagement.Api.IntegrationTests.Infrastructure;
using ProductManagement.Testing;
using static ProductManagement.Testing.ProductApiClient;

namespace ProductManagement.Api.IntegrationTests.Security;

/// <summary>
/// Role-based access control, enforced by the API. The UI hides actions a role cannot perform, but these tests are
/// the actual guarantee.
/// </summary>
public class AuthorizationTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    public enum Operation
    {
        List,
        Get,
        Search,
        StockLevel,
        Categories,
        Create,
        Update,
        Delete,
        DecrementStock,
        AddToStock,
    }

    // Who may do what. Every combination is exercised below.
    public static TheoryData<string, Operation, bool> Matrix()
    {
        var data = new TheoryData<string, Operation, bool>();
        foreach (var operation in Enum.GetValues<Operation>())
        {
            var isRead = operation is Operation.List or Operation.Get or Operation.Search or Operation.StockLevel
                or Operation.Categories;
            var isStock = operation is Operation.DecrementStock or Operation.AddToStock;

            data.Add(TestTokens.Viewer, operation, isRead);
            data.Add(TestTokens.StockClerk, operation, isRead || isStock);
            data.Add(TestTokens.Manager, operation, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EachRole_CanPerformExactlyItsPermittedOperations(string role, Operation operation, bool allowed)
    {
        var product = await Api.CreateProductAsync(TestProducts.CreateRequest(stockQuantity: 10));
        using var client = Environment.CreateClient(role);

        using var response = await SendAsync(client, operation, product.Id);

        if (allowed)
        {
            ((int)response.StatusCode).ShouldBeLessThan(400, $"{role} should be allowed to {operation}");
        }
        else
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{role} must not be allowed to {operation}");
            (await ReadProblemAsync(response)).ErrorCode.ShouldBe("http_403");
        }
    }

    [Fact]
    public async Task RequestsWithoutAToken_AreRejectedWith401()
    {
        using var anonymous = Environment.CreateClient(role: null);

        using var response = await anonymous.ListAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldStartWith("Bearer");
        (await ReadProblemAsync(response)).ErrorCode.ShouldBe("http_401");
    }

    [Fact]
    public async Task ExpiredTokens_AreRejected()
    {
        using var client = ClientWithToken(TestTokens.Create(TestTokens.Manager, expires: DateTime.UtcNow.AddMinutes(-5)));

        using var response = await client.ListAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokensIssuedForAnotherApplication_AreRejected()
    {
        using var client = ClientWithToken(TestTokens.Create(TestTokens.Manager, audience: "some-other-api"));

        using var response = await client.ListAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokensSignedWithAnUnknownKey_AreRejected()
    {
        using var forgedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var client = ClientWithToken(TestTokens.Create(TestTokens.Manager, signingKey: forgedKey));

        using var response = await client.ListAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AlgorithmConfusion_HS256SignedWithThePublicKey_IsRejected()
    {
        // The classic attack on asymmetric JWTs: take the (public) verification key and use it as an HMAC secret.
        var publicKey = TestTokens.SigningKey.ExportSubjectPublicKeyInfo();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TestTokens.Issuer,
            Audience = TestTokens.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Subject = new ClaimsIdentity([new Claim("sub", "attacker"), new Claim("roles", TestTokens.Manager)]),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(publicKey), SecurityAlgorithms.HmacSha256),
        });
        using var client = ClientWithToken(token);

        using var response = await client.ListAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnsignedTokens_AreRejected()
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(
            $$"""{"sub":"attacker","roles":"manager","iss":"{{TestTokens.Issuer}}","aud":"{{TestTokens.Audience}}","exp":{{exp}}}""");
        using var client = ClientWithToken($"{header}.{payload}.");

        using var response = await client.ListAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("health/live")]
    [InlineData("health/ready")]
    [InlineData("openapi/v1.json")]
    public async Task OperationalEndpoints_AreAnonymous(string path)
    {
        using var anonymous = Environment.CreateClient(role: null);

        using var response = await anonymous.Http.GetAsync(new Uri(path, UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private ProductApiClient ClientWithToken(string token)
    {
        var client = Environment.CreateClient(role: null);
        client.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> SendAsync(ProductApiClient client, Operation operation, int id) =>
        operation switch
        {
            Operation.List => client.ListAsync(),
            Operation.Get => client.GetAsync(id),
            Operation.Search => client.SearchAsync("test"),
            Operation.StockLevel => client.ByStockLevelAsync(0, 100),
            Operation.Categories => client.Http.GetAsync(new Uri("api/categories", UriKind.Relative)),
            Operation.Create => client.CreateAsync(TestProducts.CreateRequest(name: "Created by role test")),
            Operation.Update => client.UpdateAsync(id, new { name = "Renamed", price = 1m, stockQuantity = 1, categoryId = 1 }),
            Operation.Delete => client.DeleteAsync(id),
            Operation.DecrementStock => client.DecrementStockAsync(id, 1),
            Operation.AddToStock => client.AddToStockAsync(id, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
}
