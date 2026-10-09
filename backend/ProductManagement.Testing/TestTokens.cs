using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ProductManagement.Testing;

/// <summary>
/// Issues access tokens shaped like Keycloak's (issuer, audience, <c>sub</c>, <c>roles</c>), signed with a key that
/// only the test host trusts. The API's real JWT validation runs; only the identity provider is replaced.
/// </summary>
public static class TestTokens
{
    public const string Issuer = "https://identity.tests.local/realms/product-management";

    public const string Audience = "product-management-api";

    public const string Viewer = "viewer";

    public const string StockClerk = "stock-clerk";

    public const string Manager = "manager";

    public static SecurityKey SigningKey { get; } = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "tests" };

    public static string Create(
        string role,
        string? subject = null,
        string audience = Audience,
        DateTime? expires = null,
        SecurityKey? signingKey = null)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = expires ?? now.AddMinutes(30),
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", subject ?? $"{role}-user"),
                new Claim("preferred_username", subject ?? $"{role}-user"),
                new Claim("roles", role),
            ]),
            SigningCredentials = new SigningCredentials(signingKey ?? SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }
}
