namespace ProductManagement.Api.Security;

/// <summary>
/// JWT bearer settings. The API never sees passwords: users sign in at the identity provider (Keycloak, OIDC) and
/// the API only validates the signed access token it receives.
/// </summary>
internal sealed class AuthenticationSettings
{
    public const string SectionName = "Authentication";

    /// <summary>Issuer URL of the realm, exactly as it appears in the token's <c>iss</c> claim.</summary>
    public string Authority { get; init; } = string.Empty;

    /// <summary>
    /// Optional discovery URL the API should use instead of <c>{Authority}/.well-known/openid-configuration</c>,
    /// e.g. the in-cluster service address when the public issuer URL is only reachable from browsers.
    /// </summary>
    public string? MetadataAddress { get; init; }

    /// <summary>Expected <c>aud</c> claim: tokens issued for other applications are rejected.</summary>
    public string Audience { get; init; } = "product-management-api";

    /// <summary>Only for local clusters without TLS. Must stay <c>true</c> in production.</summary>
    public bool RequireHttpsMetadata { get; init; } = true;
}
