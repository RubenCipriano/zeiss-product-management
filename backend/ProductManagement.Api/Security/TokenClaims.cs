namespace ProductManagement.Api.Security;

/// <summary>
/// Claim names in the API's access tokens. Kept as issued (no mapping to the legacy WS-Federation URIs), so the same
/// names are written by <see cref="AccessTokenIssuer"/> and read everywhere else.
/// </summary>
public static class TokenClaims
{
    /// <summary>The API consumer's client id.</summary>
    public const string Subject = "sub";

    /// <summary>One claim per role (see <see cref="Roles"/>).</summary>
    public const string Roles = "roles";
}
