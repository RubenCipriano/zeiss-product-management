namespace ProductManagement.Infrastructure.Edge;

/// <summary>
/// Rate-limit counters shared by every API replica (in Redis), so a caller's limit is the same with 2 replicas or 8.
/// Per-replica, in-memory limits would allow <c>limit × replicas</c> as the autoscaler adds pods.
/// </summary>
/// <remarks>
/// The counters are token buckets: each caller has a bucket that refills at a steady rate up to a maximum size; every
/// request takes one token, and a request that finds the bucket empty is refused (429).
/// </remarks>
public interface IDistributedRateLimiter
{
    /// <summary>
    /// Takes one token from <paramref name="bucket"/>, which refills at <paramref name="tokensPerSecond"/> up to
    /// <paramref name="capacity"/>. Returns <c>null</c> when the shared store is unavailable: callers then fall back
    /// to a local limit rather than failing the request.
    /// </summary>
    Task<RateLimitDecision?> TryTakeAsync(
        string bucket,
        int tokensPerSecond,
        int capacity,
        CancellationToken cancellationToken);
}

/// <summary>Whether the request may go ahead, and if not, how long until a token is available.</summary>
public readonly record struct RateLimitDecision(bool Allowed, TimeSpan RetryAfter);
