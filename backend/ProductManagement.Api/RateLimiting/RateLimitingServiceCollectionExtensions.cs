using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ProductManagement.Api.RateLimiting;

internal static class RateLimitingServiceCollectionExtensions
{
    /// <remarks>
    /// Limits are kept in memory, per replica, so the effective cluster-wide limit is roughly
    /// <c>limit × replicas</c>. Coarse, cluster-wide protection is the gateway's job (nginx <c>limit_req</c>); this
    /// layer protects each replica and keeps one user from starving others. See docs/adr/0010.
    /// </remarks>
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(RateLimitingSettings.SectionName).Get<RateLimitingSettings>()
            ?? new RateLimitingSettings();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteRejectionAsync;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                !settings.Enabled || context.Request.Path.StartsWithSegments("/health")
                    ? RateLimitPartition.GetNoLimiter("unlimited")
                    : TokenBucket(PartitionKey(context), settings.RequestsPerSecond, settings.RequestBurst));

            options.AddPolicy(RateLimitPolicies.Writes, context =>
                settings.Enabled
                    ? TokenBucket(PartitionKey(context), settings.WritesPerSecond, settings.WriteBurst)
                    : RateLimitPartition.GetNoLimiter("unlimited"));

            options.AddPolicy(RateLimitPolicies.Authentication, context =>
                settings.Enabled
                    ? RateLimitPartition.GetFixedWindowLimiter($"ip:{context.Connection.RemoteIpAddress}", _ =>
                        new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = settings.TokenRequestsPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        })
                    : RateLimitPartition.GetNoLimiter("unlimited"));
        });

        return services;
    }

    /// <summary>Signed-in callers are limited per user; anonymous ones per IP address.</summary>
    private static string PartitionKey(HttpContext context) =>
        context.User.FindFirstValue("sub") is { } subject
            ? $"user:{subject}"
            : $"ip:{context.Connection.RemoteIpAddress}";

    private static RateLimitPartition<string> TokenBucket(string key, int perSecond, int burst) =>
        RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = burst,
            TokensPerPeriod = perSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        var retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : 1;
        httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var problemDetailsService = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests.",
                Detail = $"Rate limit exceeded. Retry after {retryAfterSeconds} second(s).",
                Extensions = { ["errorCode"] = "rate_limited" },
            },
        });
    }
}
