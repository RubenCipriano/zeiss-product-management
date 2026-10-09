namespace ProductManagement.Api.Security;

/// <summary>
/// The API issues and verifies its own JWT access tokens. Callers are registered <see cref="ApiConsumer"/>s that
/// exchange their credentials for a token at <c>POST /api/auth/token</c>.
/// </summary>
public sealed class AuthenticationSettings
{
    public const string SectionName = "Authentication";

    /// <summary>Minimum signing key size for HMAC-SHA256 (RFC 7518 §3.2).</summary>
    public const int MinimumSigningKeyBytes = 32;

    /// <summary><c>iss</c> claim written into, and required from, every token.</summary>
    public string Issuer { get; init; } = "product-management-api";

    /// <summary><c>aud</c> claim: tokens meant for another application are rejected.</summary>
    public string Audience { get; init; } = "product-management-api";

    /// <summary>
    /// Base64-encoded HMAC-SHA256 key (at least 32 bytes). Shared by all replicas through a Kubernetes Secret, so any
    /// replica can verify a token another one issued.
    /// </summary>
    public string SigningKey { get; init; } = string.Empty;

    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromHours(1);

    public List<ApiConsumer> Consumers { get; init; } = [];
}

/// <summary>A client allowed to call the API: the web app's users, the MCP server, load tests, other services.</summary>
public sealed class ApiConsumer
{
    public string ClientId { get; init; } = string.Empty;

    /// <summary>PBKDF2 hash of the client secret (see <see cref="SecretHasher"/>); the secret itself is never stored.</summary>
    public string SecretHash { get; init; } = string.Empty;

    /// <summary>Roles placed in the token (viewer, stock-clerk, manager); they map to permissions in <see cref="Policies"/>.</summary>
    public List<string> Roles { get; init; } = [];
}
