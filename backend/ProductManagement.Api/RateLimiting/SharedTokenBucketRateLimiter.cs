using System.Threading.RateLimiting;
using ProductManagement.Application.Common.Security;

namespace ProductManagement.Api.RateLimiting;

/// <summary>
/// A token-bucket <see cref="RateLimiter"/> whose bucket lives in the shared store (Redis), so the limit holds across
/// all replicas. If the store is unavailable it falls back to an in-memory bucket for this replica: limits get looser
/// (per replica), requests keep being served.
/// </summary>
internal sealed class SharedTokenBucketRateLimiter(
    IDistributedRateLimiter store,
    string bucket,
    int tokensPerSecond,
    int capacity) : RateLimiter
{
    private readonly TokenBucketRateLimiter _local = new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = capacity,
        TokensPerPeriod = tokensPerSecond,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    });

    public override TimeSpan? IdleDuration => _local.IdleDuration;

    public override RateLimiterStatistics? GetStatistics() => _local.GetStatistics();

    // The shared bucket needs a network call, so the synchronous path declines and ASP.NET Core's rate-limiting
    // middleware continues with AcquireAsync. A zero-permit call only asks for the state.
    protected override RateLimitLease AttemptAcquireCore(int permitCount) =>
        permitCount == 0 ? _local.AttemptAcquire(0) : Lease.Pending;

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        var decision = await store.TryTakeAsync(bucket, tokensPerSecond, capacity, cancellationToken);
        if (decision is null)
        {
            return await _local.AcquireAsync(permitCount, cancellationToken);
        }

        return decision.Value.Allowed ? Lease.Granted : Lease.Rejected(decision.Value.RetryAfter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _local.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class Lease(bool isAcquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public static readonly Lease Granted = new(true, null);

        public static readonly Lease Pending = new(false, null);

        public override bool IsAcquired { get; } = isAcquired;

        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public static Lease Rejected(TimeSpan retryAfter) => new(false, retryAfter);

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = metadataName == MetadataName.RetryAfter.Name ? retryAfter : null;
            return metadata is not null;
        }
    }
}
