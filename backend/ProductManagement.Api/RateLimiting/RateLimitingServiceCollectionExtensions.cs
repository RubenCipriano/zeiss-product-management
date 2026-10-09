using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProductManagement.Application.Common.Security;

namespace ProductManagement.Api.RateLimiting;

internal static class RateLimitingServiceCollectionExtensions
{
    /// <remarks>
    /// Per-user limits are shared by all replicas (token buckets in Redis), so a user's allowance does not grow as the
    /// autoscaler adds pods. If Redis is unavailable each replica falls back to its own in-memory bucket. The token
    /// endpoint's per-IP limit stays per replica; its cluster-wide guard is the failed-attempt throttle.
    /// See docs/adr/0010.
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
                    : TokenBucket(context, "requests", settings.RequestsPerSecond, settings.RequestBurst));

            options.AddPolicy(RateLimitPolicies.Writes, context =>
                settings.Enabled
                    ? TokenBucket(context, "writes", settings.WritesPerSecond, settings.WriteBurst)
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

    private static RateLimitPartition<string> TokenBucket(HttpContext context, string kind, int perSecond, int burst)
    {
        var bucket = $"{kind}:{PartitionKey(context)}";
        var store = context.RequestServices.GetRequiredService<IDistributedRateLimiter>();
        return RateLimitPartition.Get(bucket, key => new SharedTokenBucketRateLimiter(store, key, perSecond, burst));
    }

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
