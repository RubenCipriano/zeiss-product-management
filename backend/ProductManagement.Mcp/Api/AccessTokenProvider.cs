using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ProductManagement.Mcp.Api;

/// <summary>
/// Obtains access tokens with the OAuth 2.0 client-credentials grant and caches them until shortly before they
/// expire, so tool calls don't hit the identity provider each time. Singleton.
/// </summary>
internal sealed class AccessTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<ProductApiOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    public const string HttpClientName = "identity";

    private static readonly TimeSpan RenewBeforeExpiry = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private (string Token, DateTimeOffset RenewAt)? _cached;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } cached && cached.RenewAt > timeProvider.GetUtcNow())
        {
            return cached.Token;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed the token while we waited.
            if (_cached is { } refreshed && refreshed.RenewAt > timeProvider.GetUtcNow())
            {
                return refreshed.Token;
            }

            var token = await RequestTokenAsync(cancellationToken);
            _cached = (token.AccessToken, timeProvider.GetUtcNow().AddSeconds(token.ExpiresIn) - RenewBeforeExpiry);
            return token.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    private async Task<TokenResponse> RequestTokenAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsync(
            settings.TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret,
            }),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new ProductApiException(
                (int)response.StatusCode,
                "authentication_failed",
                $"The MCP server could not sign in to the identity provider ({(int)response.StatusCode}). " +
                "Check ProductApi__ClientId and ProductApi__ClientSecret.");
        }

        return await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new ProductApiException(500, "authentication_failed", "The identity provider returned no token.");
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
