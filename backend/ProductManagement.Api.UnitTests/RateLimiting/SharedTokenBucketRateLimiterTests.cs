using System.Threading.RateLimiting;
using NSubstitute;
using ProductManagement.Api.RateLimiting;
using ProductManagement.Infrastructure.Edge;

namespace ProductManagement.Api.UnitTests.RateLimiting;

public sealed class SharedTokenBucketRateLimiterTests
{
    private readonly IDistributedRateLimiter _store = Substitute.For<IDistributedRateLimiter>();

    [Fact]
    public async Task TheSharedBucketDecides_WhenTheStoreIsAvailable()
    {
        _store.TryTakeAsync("writes:user:maria", 1, 2, Arg.Any<CancellationToken>())
            .Returns(new RateLimitDecision(true, TimeSpan.Zero), new RateLimitDecision(false, TimeSpan.FromMilliseconds(1500)));
        using var limiter = new SharedTokenBucketRateLimiter(_store, "writes:user:maria", tokensPerSecond: 1, capacity: 2);

        using var granted = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        using var rejected = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        granted.IsAcquired.ShouldBeTrue();
        rejected.IsAcquired.ShouldBeFalse();
        rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).ShouldBeTrue();
        retryAfter.ShouldBe(TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public async Task WhenTheStoreIsUnavailable_ALocalBucketWithTheSameLimitsApplies()
    {
        _store.TryTakeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((RateLimitDecision?)null);
        using var limiter = new SharedTokenBucketRateLimiter(_store, "writes:user:maria", tokensPerSecond: 1, capacity: 2);

        using var first = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        using var second = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);
        using var third = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        first.IsAcquired.ShouldBeTrue();
        second.IsAcquired.ShouldBeTrue();
        third.IsAcquired.ShouldBeFalse(); // the local bucket holds 2 tokens
    }

    [Fact]
    public void TheSynchronousPath_DefersToTheSharedBucket()
    {
        using var limiter = new SharedTokenBucketRateLimiter(_store, "writes:user:maria", tokensPerSecond: 1, capacity: 2);

        using var lease = limiter.AttemptAcquire(1);

        lease.IsAcquired.ShouldBeFalse();
        lease.TryGetMetadata(MetadataName.RetryAfter, out _).ShouldBeFalse();
        _store.ReceivedCalls().ShouldBeEmpty();
    }
}
