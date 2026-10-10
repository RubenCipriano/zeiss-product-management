using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ProductManagement.Api.Configuration;
using ProductManagement.Api.Hosting;
using ProductManagement.Api.Observability;
using ProductManagement.Api.RateLimiting;
using ProductManagement.Api.Security;
using ProductManagement.Core;
using ProductManagement.Infrastructure;
using ProductManagement.Infrastructure.Persistence;

// The API's entry point. It registers the services of the three projects (Core, Infrastructure, Api) in the
// dependency-injection container, builds the HTTP pipeline (middleware: each request passes through the app.Use...
// steps below, in order), brings the database up to date and starts serving requests.

// Command-line tools (hash-secret, generate-signing-key, migrate) run and exit without starting the web host.
if (await ApiCommandLine.TryRunAsync(args) is { } exitCode)
{
    return exitCode;
}

var builder = WebApplication.CreateBuilder(args);

// Registered API consumers can also come from a file, e.g. mounted from a Kubernetes Secret (the deploy repository).
builder.Configuration.AddJsonFile("config/consumers.json", optional: true, reloadOnChange: false);

// JSON logs with trace ids on stdout; traces and metrics over OTLP when configured.
builder.AddApiLogging();

// Development and the in-memory test host ("Testing") may run without Redis; a deployment with replicas may not.
var requireRedis = !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing");

builder.Services
    .AddCore()
    .AddInfrastructure(builder.Configuration, requireRedis)
    .AddApiPresentation()
    .AddApiSecurity()
    .AddApiRateLimiting();

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

// Order matters: who the caller is (authentication, from the bearer token) must be known before rate limiting (limits
// are per user) and before authorization (is this caller allowed to call this endpoint?).
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

await app.Services.InitializeDatabaseAsync();
await app.RunAsync();
return 0;
