using System.ComponentModel.DataAnnotations;

namespace ProductManagement.Api.Security;

/// <summary>
/// The API issues and verifies its own JWT access tokens. Callers are registered <see cref="ApiConsumer"/>s that
/// exchange their credentials for a token at <c>POST /api/auth/token</c>. Validated at startup
/// (<see cref="AuthenticationOptionsValidator"/>).
/// </summary>
public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    /// <summary><c>iss</c> claim written into, and required from, every token.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Issuer { get; init; } = "product-management-api";

    /// <summary><c>aud</c> claim: tokens meant for another application are rejected.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Audience { get; init; } = "product-management-api";

    /// <summary>
    /// PEM-encoded ECDSA P-256 private key (ES256). Shared by all replicas through a Kubernetes Secret, so any replica
    /// can verify a token another one issued. Only the API holds it; verifiers use the public key (JWKS).
    /// Generate one with <c>openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256</c>.
    /// </summary>
    public string SigningKey { get; init; } = string.Empty;

    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
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
