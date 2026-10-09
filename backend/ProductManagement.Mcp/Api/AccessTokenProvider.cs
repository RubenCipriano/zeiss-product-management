using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace ProductManagement.Mcp.Api;

/// <summary>
/// Obtains access tokens from the API's token endpoint (client id + secret of the MCP server's consumer) and caches
/// them until shortly before they expire, so tool calls don't request a token each time. Singleton.
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
        using var response = await client.PostAsJsonAsync(
            new Uri(settings.BaseUrl, settings.TokenPath),
            new { clientId = settings.ClientId, clientSecret = settings.ClientSecret },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new ProductApiException(
                (int)response.StatusCode,
                "authentication_failed",
                $"The MCP server could not get a token from the API ({(int)response.StatusCode}). " +
                "Check ProductApi__ClientId and ProductApi__ClientSecret.");
        }

        return await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new ProductApiException(500, "authentication_failed", "The API returned no token.");
    }

    private sealed record TokenResponse(string AccessToken, int ExpiresIn);
}
