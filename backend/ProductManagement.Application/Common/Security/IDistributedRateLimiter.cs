namespace ProductManagement.Application.Common.Security;

/// <summary>
/// Token buckets shared by every API replica, so a caller's limit is the same with 2 replicas or 8. Per-replica,
/// in-memory limits would allow <c>limit × replicas</c> as the autoscaler adds pods.
/// </summary>
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

public readonly record struct RateLimitDecision(bool Allowed, TimeSpan RetryAfter);
