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

    /// <summary>The API's token endpoint, relative to <see cref="BaseUrl"/>.</summary>
    public string TokenPath { get; init; } = "api/auth/token";

    /// <summary>
    /// The MCP server's own identity: a registered API consumer with the <c>stock-clerk</c> role. An assistant can read
    /// the catalogue and record stock movements, but cannot create, edit or delete products.
    /// </summary>
    public string ClientId { get; init; } = "mcp-server";

    public string ClientSecret { get; init; } = string.Empty;
}
