using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using ProductManagement.Api.ErrorHandling;
using ProductManagement.Api.Http;

namespace ProductManagement.Api.Configuration;

/// <summary>
/// Registers the HTTP side of the API at startup (<c>AddApiPresentation</c> in <c>Program.cs</c>): controllers and
/// JSON, the shape of error responses, the OpenAPI document, CORS and the trusted proxy headers.
/// </summary>
internal static class ApiServiceCollectionExtensions
{
    private const string BearerScheme = "Bearer";

    public static IServiceCollection AddApiPresentation(this IServiceCollection services)
    {
        AddControllersWithJson(services);
        AddProblemDetails(services);
        AddOpenApiDocument(services);
        AddCorsPolicy(services);
        AddForwardedHeaders(services);
        return services;
    }

    private static void AddControllersWithJson(IServiceCollection services) =>
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

    /// <summary>
    /// Every error response is a "problem details" JSON object (RFC 9457, <c>application/problem+json</c>: the
    /// standard shape for HTTP errors) with an extra machine-readable <c>errorCode</c>. See ADR 0005.
    /// </summary>
    private static void AddProblemDetails(IServiceCollection services)
    {
        services.Configure<ApiBehaviorOptions>(options =>
        {
            // Model-binding failures (malformed JSON, "page=abc") use the same problem shape as use-case validation
            // errors, including the machine-readable errorCode.
            options.InvalidModelStateResponseFactory = context =>
            {
                RemoveRedundantBodyErrors(context);

                var problemDetailsFactory = context.HttpContext.RequestServices
                    .GetRequiredService<ProblemDetailsFactory>();
                var problem = problemDetailsFactory
                    .CreateValidationProblemDetails(
                        context.HttpContext,
                        context.ModelState,
                        StatusCodes.Status400BadRequest)
                    .WithErrorCode(ApiErrorCodes.ValidationFailed);
                UseCamelCaseKeys(problem);

                return new BadRequestObjectResult(problem)
                {
                    ContentTypes = { ApiProblems.ContentType, MediaTypeNames.Application.Json },
                };
            };
        });

        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Instance ??=
                    $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";

                // Every problem carries an errorCode. Framework-generated ones (unknown route, 405, 415, ...)
                // get a generic "http_<status>" code; use-case errors set their own, more specific code.
                context.ProblemDetails.Extensions.TryAdd(
                    ApiProblems.ErrorCodeKey,
                    ApiErrorCodes.ForStatus(context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode));
                context.HttpContext.Features.Get<IHttpActivityFeature>()?.Activity
                    .SetTag("problem.status", context.ProblemDetails.Status);
            });
        services.AddExceptionHandler<GlobalExceptionHandler>();
    }

    /// <summary>The OpenAPI (Swagger) description of the endpoints, including the bearer-token requirement.</summary>
    private static void AddOpenApiDocument(IServiceCollection services) =>
        services.AddOpenApi(options =>
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Product Management API";
                document.Info.Description =
                    "Manage products and their stock. Errors follow RFC 9457 (application/problem+json) and carry a " +
                    "machine-readable `errorCode`. Conditional updates use ETag / If-Match. All endpoints require " +
                    "a bearer token: POST your client id and secret to /api/auth/token " +
                    "(roles: viewer, stock-clerk, manager).";

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
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(BearerScheme, document)] = [],
                    },
                ];
                return Task.CompletedTask;
            }));

    /// <summary>
    /// CORS: which other websites' pages a browser lets call this API. Off unless origins are configured, because the
    /// web app is served from the same origin.
    /// </summary>
    private static void AddCorsPolicy(IServiceCollection services)
    {
        services.AddOptions<CorsPolicyOptions>()
            .BindConfiguration(CorsPolicyOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddCors();
        services.AddOptions<CorsOptions>().Configure<IOptions<CorsPolicyOptions>>((options, origins) =>
            options.AddDefaultPolicy(policy =>
            {
                if (origins.Value.AllowedOrigins.Length > 0)
                {
                    policy
                        .WithOrigins(origins.Value.AllowedOrigins)
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .WithExposedHeaders("ETag", "Location");
                }
            }));
    }

    private static void AddForwardedHeaders(IServiceCollection services) =>
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            // The API is only reachable through the gateway (Kubernetes NetworkPolicy), so the client address it
            // forwards in X-Forwarded-For can be trusted; it is what rate limiting partitions anonymous callers by.
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

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

    /// <summary>
    /// Keys the errors the way the client spelled the field, like use-case validation errors do: a query object's
    /// properties bind under their C# names ("Min"), but the client sent "min". JSON paths ("$.price") stay as they
    /// are.
    /// </summary>
    private static void UseCamelCaseKeys(ValidationProblemDetails problem)
    {
        var errors = problem.Errors
            .GroupBy(error => JsonNamingPolicy.CamelCase.ConvertName(error.Key))
            .ToDictionary(group => group.Key, group => group.SelectMany(error => error.Value).ToArray());

        problem.Errors.Clear();
        foreach (var (field, messages) in errors)
        {
            problem.Errors[field] = messages;
        }
    }
}
