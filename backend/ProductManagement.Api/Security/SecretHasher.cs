using System.Globalization;
using System.Security.Cryptography;

namespace ProductManagement.Api.Security;

/// <summary>
/// Hashes consumer secrets with PBKDF2-HMAC-SHA256 (salted, 100,000 iterations), so configuration holds no usable
/// secrets. Format: <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt;</c> (Base64).
/// Generate a hash with: <c>dotnet ProductManagement.Api.dll hash-secret &lt;secret&gt;</c>.
/// </summary>
public static class SecretHasher
{
    private const string Algorithm = "pbkdf2-sha256";
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Algorithm, Iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string secret, string encodedHash)
    {
        var parts = encodedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Algorithm
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations))
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // Constant-time comparison: response time reveals nothing about how close a guess was.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static bool IsWellFormed(string encodedHash) => encodedHash.Split('$') is [Algorithm, _, _, _];
}
