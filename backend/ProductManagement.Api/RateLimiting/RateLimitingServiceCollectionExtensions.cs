using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ProductManagement.Api.Http;
using ProductManagement.Api.Security;
using ProductManagement.Infrastructure.Edge;

namespace ProductManagement.Api.RateLimiting;

/// <summary>
/// Registers the rate limits at startup (<c>AddApiRateLimiting</c> in <c>Program.cs</c>): a global per-caller limit,
/// a stricter one for writes, and a per-IP one for the token endpoint. Over the limit, the answer is 429 with
/// <c>Retry-After</c>.
/// </summary>
internal static class RateLimitingServiceCollectionExtensions
{
    private const string NoLimitPartition = "unlimited";

    /// <remarks>
    /// Per-user limits are shared by all replicas (token buckets in Redis), so a user's allowance does not grow as the
    /// autoscaler adds pods. If Redis is unavailable each replica falls back to its own in-memory bucket. The token
    /// endpoint's per-IP limit stays per replica; its cluster-wide guard is the failed-attempt throttle.
    /// See ADR 0010.
    /// </remarks>
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitingOptions>()
            .BindConfiguration(RateLimitingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitingOptions>>((options, limits) =>
        {
            var settings = limits.Value;
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteRejectionAsync;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                !settings.Enabled || context.Request.Path.StartsWithSegments("/health")
                    ? RateLimitPartition.GetNoLimiter(NoLimitPartition)
                    : TokenBucket(context, "requests", settings.RequestsPerSecond, settings.RequestBurst));

            options.AddPolicy(RateLimitPolicies.Writes, context =>
                settings.Enabled
                    ? TokenBucket(context, "writes", settings.WritesPerSecond, settings.WriteBurst)
                    : RateLimitPartition.GetNoLimiter(NoLimitPartition));

            options.AddPolicy(RateLimitPolicies.Authentication, context =>
                settings.Enabled
                    ? RateLimitPartition.GetFixedWindowLimiter(context.GetClientIpKey(), _ =>
                        new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = settings.TokenRequestsPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        })
                    : RateLimitPartition.GetNoLimiter(NoLimitPartition));
        });

        return services;
    }

    /// <summary>Signed-in callers are limited per user; anonymous ones per IP address.</summary>
    private static string PartitionKey(HttpContext context) =>
        context.GetSubject() is { } subject ? $"user:{subject}" : context.GetClientIpKey();

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
            }.WithErrorCode(ApiErrorCodes.RateLimited),
        });
    }
}
