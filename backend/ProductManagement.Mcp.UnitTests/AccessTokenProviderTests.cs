using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ProductManagement.Mcp.Api;

namespace ProductManagement.Mcp.UnitTests;

public sealed class AccessTokenProviderTests : IDisposable
{
    private readonly StubHttpHandler _identity = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly AccessTokenProvider _provider;

    public AccessTokenProviderTests() =>
        _provider = new AccessTokenProvider(
            new SingleClientFactory(_identity),
            Options.Create(new ProductApiOptions { ClientSecret = "secret" }),
            _time);

    public void Dispose()
    {
        _identity.Dispose();
        _provider.Dispose();
    }

    [Fact]
    public async Task RequestsATokenWithTheClientCredentialsGrant()
    {
        _identity.Respond(HttpStatusCode.OK, """{"access_token":"token-1","expires_in":300}""");

        var token = await _provider.GetTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBe("token-1");
        var body = await _identity.Requests.Single().Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("grant_type=client_credentials");
        body.ShouldContain("client_id=product-management-mcp");
    }

    [Fact]
    public async Task ReusesTheTokenUntilShortlyBeforeItExpires()
    {
        _identity
            .Respond(HttpStatusCode.OK, """{"access_token":"token-1","expires_in":300}""")
            .Respond(HttpStatusCode.OK, """{"access_token":"token-2","expires_in":300}""");

        var first = await _provider.GetTokenAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(260)); // still valid (renewed 30 s before the 300 s expiry)
        var second = await _provider.GetTokenAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(20)); // now inside the renewal window
        var third = await _provider.GetTokenAsync(TestContext.Current.CancellationToken);

        (first, second, third).ShouldBe(("token-1", "token-1", "token-2"));
        _identity.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneTokenRequest()
    {
        _identity.Respond(HttpStatusCode.OK, """{"access_token":"token-1","expires_in":300}""");

        var tokens = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => _provider.GetTokenAsync(TestContext.Current.CancellationToken)));

        tokens.ShouldAllBe(token => token == "token-1");
        _identity.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RejectedCredentials_ExplainWhatToCheck()
    {
        _identity.Respond(HttpStatusCode.Unauthorized, """{"error":"unauthorized_client"}""");

        var exception = await Should.ThrowAsync<ProductApiException>(() =>
            _provider.GetTokenAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("ProductApi__ClientSecret");
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
