namespace ProductManagement.Api.Configuration;

/// <summary>
/// CORS settings (the <c>Cors</c> section of the configuration): which other websites' pages a browser lets call this
/// API.
/// </summary>
internal class CorsPolicyOptions
{
    public const string SectionName = "Cors";

    /// <summary>
    /// Browser origins allowed to call the API directly. Leave empty when the frontend is served from the same
    /// origin (reverse proxy or dev-server proxy), which is the default setup.
    /// </summary>
    public string[] AllowedOrigins { get; init; } = [];
}
