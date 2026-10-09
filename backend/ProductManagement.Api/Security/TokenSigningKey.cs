using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace ProductManagement.Api.Security;

/// <summary>
/// The key the API signs access tokens with: ECDSA P-256 (ES256). The private key never leaves the API; anyone else
/// that verifies tokens (the gateway, other services) gets only the public key, from <c>/.well-known/jwks.json</c>.
/// </summary>
/// <remarks>
/// <see cref="KeyId"/> is the RFC 7638 thumbprint of the public key, written into every token's header (<c>kid</c>).
/// Rotating the key changes it, so verifiers know which key a token needs.
/// </remarks>
public sealed class TokenSigningKey : IDisposable
{
    private readonly ECDsa _privateKey;
    private readonly ECDsa _publicKey;

    private TokenSigningKey(ECDsa privateKey)
    {
        _privateKey = privateKey;
        var parameters = privateKey.ExportParameters(includePrivateParameters: false);
        _publicKey = ECDsa.Create(parameters);

        var x = Base64UrlEncoder.Encode(parameters.Q.X);
        var y = Base64UrlEncoder.Encode(parameters.Q.Y);
        // RFC 7638: SHA-256 of the required members, in lexicographic order, without whitespace.
        KeyId = Base64UrlEncoder.Encode(
            SHA256.HashData(Encoding.UTF8.GetBytes($$"""{"crv":"P-256","kty":"EC","x":"{{x}}","y":"{{y}}"}""")));
        PublicJwk = new PublicJsonWebKey("EC", "P-256", x, y, KeyId, SecurityAlgorithms.EcdsaSha256, "sig");

        SigningCredentials = new SigningCredentials(
            new ECDsaSecurityKey(_privateKey) { KeyId = KeyId },
            SecurityAlgorithms.EcdsaSha256);
        VerificationKey = new ECDsaSecurityKey(_publicKey) { KeyId = KeyId };
    }

    public string KeyId { get; }

    public SigningCredentials SigningCredentials { get; }

    /// <summary>Public half only: what token validation uses.</summary>
    public SecurityKey VerificationKey { get; }

    public PublicJsonWebKey PublicJwk { get; }

    /// <summary>Loads a PEM-encoded P-256 private key (PKCS#8 or SEC1). Fails fast on anything else.</summary>
    public static TokenSigningKey FromPem(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            throw new InvalidOperationException(
                $"'{AuthenticationOptions.SectionName}:SigningKey' is missing: a PEM-encoded ECDSA P-256 private key.");
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            key.Dispose();
            throw new InvalidOperationException(
                $"'{AuthenticationOptions.SectionName}:SigningKey' is not a valid PEM-encoded ECDSA private key.",
                exception);
        }

        if (key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
        {
            key.Dispose();
            throw new InvalidOperationException(
                $"'{AuthenticationOptions.SectionName}:SigningKey' must be a P-256 key (ES256).");
        }

        return new TokenSigningKey(key);
    }

    public void Dispose()
    {
        _privateKey.Dispose();
        _publicKey.Dispose();
    }
}

/// <summary>A public key in JWK form (RFC 7517), as published in the JWKS document. Never contains the private part.</summary>
public sealed record PublicJsonWebKey(string Kty, string Crv, string X, string Y, string Kid, string Alg, string Use);

/// <summary>The JWKS document (RFC 7517 §5).</summary>
public sealed record JsonWebKeySet(IReadOnlyList<PublicJsonWebKey> Keys);
