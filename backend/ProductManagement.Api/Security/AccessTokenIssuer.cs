using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ProductManagement.Api.Security;

/// <summary>A freshly signed access token and how many seconds it stays valid.</summary>
public record IssuedToken(string AccessToken, int ExpiresInSeconds);

/// <summary>
/// Checks a consumer's credentials and issues a signed access token (a JWT) carrying its roles. One instance for the
/// whole app (singleton).
/// </summary>
public class AccessTokenIssuer(
    IOptions<AuthenticationOptions> options,
    TokenSigningKey signingKey,
    TimeProvider timeProvider)
{
    // Verified against when the client id is unknown, so "unknown consumer" and "wrong secret" take the same time
    // and an attacker cannot discover which client ids exist.
    private static readonly string DummyHash = SecretHasher.Hash(Guid.NewGuid().ToString());

    private readonly AuthenticationOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public IssuedToken? Authenticate(string clientId, string clientSecret)
    {
        var consumer = _options.Consumers.Find(c => string.Equals(c.ClientId, clientId, StringComparison.Ordinal));
        var secretIsValid = SecretHasher.Verify(clientSecret, consumer?.SecretHash ?? DummyHash);
        if (consumer is null || !secretIsValid)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var claims = new List<Claim>
        {
            new(TokenClaims.Subject, consumer.ClientId),
            new("jti", Guid.NewGuid().ToString("N")),
        };
        claims.AddRange(consumer.Roles.Select(role => new Claim(TokenClaims.Roles, role)));

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(_options.AccessTokenLifetime),
            SigningCredentials = signingKey.SigningCredentials, // ES256, with the key id (kid) in the header
        });

        return new IssuedToken(token, (int)_options.AccessTokenLifetime.TotalSeconds);
    }
}
