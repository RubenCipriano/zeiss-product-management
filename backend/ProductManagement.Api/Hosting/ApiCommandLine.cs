using System.Security.Cryptography;
using ProductManagement.Api.Security;

namespace ProductManagement.Api.Hosting;

/// <summary>
/// The command-line tools built into the API (<c>hash-secret</c>, <c>generate-signing-key</c>): they print what an
/// operator needs to configure the API, and exit instead of serving HTTP. The database needs no command: the API
/// applies its migrations itself when it starts (<c>Database:ApplyMigrationsOnStartup</c>).
/// </summary>
internal static class ApiCommandLine
{
    /// <summary>
    /// Runs the command in <paramref name="args"/>, if any, and returns its exit code; <c>null</c> to serve HTTP.
    /// </summary>
    public static int? TryRun(string[] args)
    {
        switch (args)
        {
            // `dotnet ProductManagement.Api.dll hash-secret <secret>`: prints the hash to put in an API consumer's
            // SecretHash.
            case ["hash-secret", var secret]:
                Console.WriteLine(SecretHasher.Hash(secret));
                return 0;

            // `dotnet ProductManagement.Api.dll generate-signing-key`: prints a new ES256 (P-256) private key for
            // Authentication:SigningKey, in PEM.
            case ["generate-signing-key"]:
                using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    Console.WriteLine(key.ExportPkcs8PrivateKeyPem());
                }

                return 0;

            default:
                return null;
        }
    }
}
