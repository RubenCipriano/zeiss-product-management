using System.Net.Http.Headers;

namespace ProductManagement.Mcp.Api;

/// <summary>Adds the MCP server's access token to every API request.</summary>
internal sealed class BearerTokenHandler(AccessTokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(cancellationToken));
        return await base.SendAsync(request, cancellationToken);
    }
}
