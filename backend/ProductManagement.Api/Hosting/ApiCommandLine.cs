using System.Security.Cryptography;
using ProductManagement.Api.Security;
using ProductManagement.Infrastructure;
using ProductManagement.Infrastructure.Persistence;

namespace ProductManagement.Api.Hosting;

/// <summary>
/// The commands the API image runs besides serving HTTP. Each builds only what it needs: <c>migrate</c> runs in the
/// Kubernetes migration Job, which therefore gets the database credentials but not the token signing key.
/// </summary>
internal static partial class ApiCommandLine
{
    private const string MigrateCommand = "migrate";

    /// <summary>Runs the command in <paramref name="args"/>, if any, and returns its exit code; <c>null</c> to serve HTTP.</summary>
    public static async Task<int?> TryRunAsync(string[] args)
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
        }

        // `dotnet ProductManagement.Api.dll migrate`: apply migrations and exit. Used by the Kubernetes migration Job,
        // so schema changes run once per deployment instead of on every replica start.
        if (args.Contains(MigrateCommand, StringComparer.OrdinalIgnoreCase))
        {
            return await MigrateAsync(args);
        }

        return null;
    }

    /// <summary>
    /// A plain host with configuration, logging and the infrastructure services: no web server, authentication or
    /// rate limiting, so it needs only the database (and, to invalidate the cache, Redis).
    /// </summary>
    private static async Task<int> MigrateAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            // Same environment as the web host, which reads ASPNETCORE_ENVIRONMENT (e.g. from launchSettings.json).
            EnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
        });
        builder.Services.AddInfrastructure(builder.Configuration);

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ApiCommandLine));
        try
        {
            await host.Services.MigrateDatabaseAsync();
            return 0;
        }
        catch (Exception exception)
        {
            // Exit cleanly with a failure code so the Job controller retries it.
            LogMigrationFailed(logger, exception);
            return 1;
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Database migration failed")]
    private static partial void LogMigrationFailed(ILogger logger, Exception exception);
}
