using System.Globalization;
using System.Net.Mime;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.OpenApi;
using ProductManagement.Api.ErrorHandling;
using ProductManagement.Api.Http;

namespace ProductManagement.Api.Configuration;

internal static class ApiServiceCollectionExtensions
{
    private const string BearerScheme = "Bearer";

    public static IServiceCollection AddApiPresentation(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddControllers()
            .AddJsonOptions(options =>
            {
                // Reject "price": "12" instead of silently converting it; the contract is JSON numbers.
                options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict;
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());

                // Don't echo serializer internals (CLR type names) back to clients.
                options.AllowInputFormatterExceptionMessages = false;
            });

        services.Configure<ApiBehaviorOptions>(options =>
        {
            // Model-binding failures (malformed JSON, "page=abc") use the same problem shape as application
            // validation errors, including the machine-readable errorCode.
            options.InvalidModelStateResponseFactory = context =>
            {
                RemoveRedundantBodyErrors(context);

                var problemDetailsFactory = context.HttpContext.RequestServices
                    .GetRequiredService<Microsoft.AspNetCore.Mvc.Infrastructure.ProblemDetailsFactory>();
                var problem = problemDetailsFactory.CreateValidationProblemDetails(
                    context.HttpContext,
                    context.ModelState,
                    StatusCodes.Status400BadRequest);
                problem.Extensions["errorCode"] = "validation_failed";

                return new BadRequestObjectResult(problem)
                {
                    ContentTypes = { ApiControllerBase.ProblemJsonContentType, MediaTypeNames.Application.Json },
                };
            };
        });

        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Instance ??=
                    $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";

                // Every problem carries an errorCode. Framework-generated ones (unknown route, 405, 415, ...)
                // get a generic "http_<status>" code; application errors set their own, more specific code.
                context.ProblemDetails.Extensions.TryAdd(
                    "errorCode",
                    string.Create(CultureInfo.InvariantCulture, $"http_{context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode}"));
                context.HttpContext.Features.Get<IHttpActivityFeature>()?.Activity
                    .SetTag("problem.status", context.ProblemDetails.Status);
            });
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.AddOpenApi(options =>
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Product Management API";
                document.Info.Description =
                    "Manage products and their stock. Errors follow RFC 9457 (application/problem+json) and carry a " +
                    "machine-readable `errorCode`. Conditional updates use ETag / If-Match. All endpoints require " +
                    "a bearer token: POST your client id and secret to /api/auth/token (roles: viewer, stock-clerk, manager).";

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Access token from POST /api/auth/token.",
                };
                document.Security =
                [
                    new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BearerScheme, document)] = [] },
                ];
                return Task.CompletedTask;
            }));

        var corsSettings = configuration.GetSection(CorsSettings.SectionName).Get<CorsSettings>() ?? new CorsSettings();
        services.AddCors(options => options.AddDefaultPolicy(policy =>
        {
            if (corsSettings.AllowedOrigins.Length > 0)
            {
                policy
                    .WithOrigins(corsSettings.AllowedOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .WithExposedHeaders("ETag", "Location");
            }
        }));

        return services;
    }

    /// <summary>
    /// When the JSON body is missing or cannot be deserialized, MVC reports the precise error ("$.field", or ""
    /// for an empty body) and also a generic "the request field is required" error for the body parameter.
    /// Only the precise one helps the client.
    /// </summary>
    private static void RemoveRedundantBodyErrors(ActionContext context)
    {
        var hasPreciseBodyErrors = context.ModelState.Keys.Any(key => key.Length == 0 || key.StartsWith('$'));
        if (!hasPreciseBodyErrors)
        {
            return;
        }

        var bodyParameterNames = context.ActionDescriptor.Parameters
            .Where(parameter => parameter.BindingInfo?.BindingSource == BindingSource.Body)
            .Select(parameter => parameter.Name);

        foreach (var name in bodyParameterNames)
        {
            context.ModelState.Remove(name);
        }
    }
}
