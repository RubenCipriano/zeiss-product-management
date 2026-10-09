using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ProductManagement.Api.Configuration;
using ProductManagement.Application;
using ProductManagement.Infrastructure;
using ProductManagement.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration)
    .AddApiPresentation(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Product Management API v1");
        options.DocumentTitle = "Product Management API";
    });
}

app.UseCors();

app.MapControllers();

// Liveness: the process is up. Readiness: it can also reach the database.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(InfrastructureServiceCollectionExtensions.DatabaseHealthCheckTag),
});

await app.Services.InitializeDatabaseAsync();
await app.RunAsync();
