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
    public async Task RequestsATokenFromTheApiWithTheConsumerCredentials()
    {
        _identity.Respond(HttpStatusCode.OK, """{"accessToken":"token-1","tokenType":"Bearer","expiresIn":300}""");

        var token = await _provider.GetTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBe("token-1");
        var body = await _identity.Requests.Single().Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        _identity.Requests.Single().RequestUri!.AbsoluteUri.ShouldBe("http://localhost:8080/api/auth/token");
        body.ShouldContain("\"clientId\":\"mcp-server\"");
        body.ShouldContain("\"clientSecret\":\"secret\"");
    }

    [Fact]
    public async Task ReusesTheTokenUntilShortlyBeforeItExpires()
    {
        _identity
            .Respond(HttpStatusCode.OK, """{"accessToken":"token-1","tokenType":"Bearer","expiresIn":300}""")
            .Respond(HttpStatusCode.OK, """{"accessToken":"token-2","tokenType":"Bearer","expiresIn":300}""");

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
        _identity.Respond(HttpStatusCode.OK, """{"accessToken":"token-1","tokenType":"Bearer","expiresIn":300}""");

        var tokens = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => _provider.GetTokenAsync(TestContext.Current.CancellationToken)));

        tokens.ShouldAllBe(token => token == "token-1");
        _identity.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RejectedCredentials_ExplainWhatToCheck()
    {
        _identity.Respond(HttpStatusCode.Unauthorized, """{"status":401,"errorCode":"invalid_client"}""");

        var exception = await Should.ThrowAsync<ProductApiException>(() =>
            _provider.GetTokenAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("ProductApi__ClientSecret");
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
