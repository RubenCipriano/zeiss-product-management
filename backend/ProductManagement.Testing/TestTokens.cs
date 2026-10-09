using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ProductManagement.Testing;

/// <summary>
/// Builds access tokens exactly like the API's token endpoint (issuer, audience, <c>sub</c>, <c>roles</c>, HMAC-SHA256),
/// signed with the key the test host is configured with. Lets tests pick any role, expiry or key without going
/// through the token endpoint (which has its own tests).
/// </summary>
public static class TestTokens
{
    public const string Issuer = "product-management-api";

    public const string Audience = "product-management-api";

    public const string Viewer = "viewer";

    public const string StockClerk = "stock-clerk";

    public const string Manager = "manager";

    /// <summary>A registered consumer the test host knows (for token endpoint tests).</summary>
    public const string ConsumerId = "test-consumer";

    public const string ConsumerSecret = "test-consumer-secret";

    public static byte[] SigningKeyBytes { get; } = RandomNumberGenerator.GetBytes(32);

    public static string SigningKeyBase64 => Convert.ToBase64String(SigningKeyBytes);

    public static string Create(
        string role,
        string? subject = null,
        string audience = Audience,
        DateTime? expires = null,
        byte[]? signingKey = null)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            IssuedAt = now.AddMinutes(-10),
            NotBefore = now.AddMinutes(-10),
            Expires = expires ?? now.AddMinutes(30),
            Subject = new ClaimsIdentity([new Claim("sub", subject ?? $"{role}-user"), new Claim("roles", role)]),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(signingKey ?? SigningKeyBytes),
                SecurityAlgorithms.HmacSha256),
        });
    }
}
