using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using ProductManagement.Api.Configuration;
using ProductManagement.Api.RateLimiting;
using ProductManagement.Api.Security;
using ProductManagement.Application;
using ProductManagement.Infrastructure;
using ProductManagement.Infrastructure.Persistence;

// `dotnet ProductManagement.Api.dll hash-secret <secret>`: prints the hash to put in an API consumer's SecretHash.
if (args is ["hash-secret", var secretToHash])
{
    Console.WriteLine(SecretHasher.Hash(secretToHash));
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Registered API consumers can also come from a file, e.g. mounted from a Kubernetes Secret (see deploy/).
builder.Configuration.AddJsonFile("config/consumers.json", optional: true, reloadOnChange: false);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddApiPresentation(builder.Configuration)
    .AddApiSecurity(builder.Configuration)
    .AddApiRateLimiting(builder.Configuration);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // The API is only reachable through the gateway (Kubernetes NetworkPolicy), so the client address it
    // forwards in X-Forwarded-For can be trusted; it is what rate limiting partitions anonymous callers by.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi().AllowAnonymous();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Product Management API v1");
        options.DocumentTitle = "Product Management API";
    });
}

app.UseCors();

// Order matters: who the caller is (authentication) must be known before rate limiting (limits are per user)
// and before authorization.
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();

// Liveness: the process is up. Readiness: it can also reach its dependencies. Probes are anonymous.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(InfrastructureServiceCollectionExtensions.ReadinessHealthCheckTag),
}).AllowAnonymous();

// `dotnet ProductManagement.Api.dll migrate`: apply migrations and exit. Used by the Kubernetes migration Job, so
// schema changes run once per deployment instead of on every replica start.
const string MigrateCommand = "migrate";
if (args.Contains(MigrateCommand, StringComparer.OrdinalIgnoreCase))
{
    try
    {
        await app.Services.MigrateDatabaseAsync();
    }
    catch (Exception exception)
    {
        // Exit cleanly with a failure code so the Job controller retries it.
        app.Logger.LogCritical(exception, "Database migration failed");
        Environment.ExitCode = 1;
    }

    return;
}

await app.Services.InitializeDatabaseAsync();
await app.RunAsync();
