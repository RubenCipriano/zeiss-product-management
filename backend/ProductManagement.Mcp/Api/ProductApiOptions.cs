namespace ProductManagement.Mcp.Api;

/// <summary>
/// Where the API is and how the MCP server signs in to it. Set through environment variables
/// (<c>ProductApi__BaseUrl</c>, <c>ProductApi__ClientSecret</c>, ...), e.g. in <c>.mcp.json</c>.
/// </summary>
internal sealed class ProductApiOptions
{
    public const string SectionName = "ProductApi";

    /// <summary>Gateway URL; the API lives under <c>/api</c>.</summary>
    public Uri BaseUrl { get; init; } = new("http://localhost:8080/");

    /// <summary>OAuth 2.0 token endpoint of the identity provider.</summary>
    public Uri TokenEndpoint { get; init; } =
        new("http://localhost:8080/auth/realms/product-management/protocol/openid-connect/token");

    /// <summary>
    /// The MCP server's own identity (a Keycloak service account). It has the <c>stock-clerk</c> role: an assistant
    /// can read the catalogue and record stock movements, but cannot create, edit or delete products.
    /// </summary>
    public string ClientId { get; init; } = "product-management-mcp";

    public string ClientSecret { get; init; } = string.Empty;
}
