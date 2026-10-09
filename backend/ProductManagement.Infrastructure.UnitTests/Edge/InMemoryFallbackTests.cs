using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ProductManagement.Infrastructure.Edge;
using ProductManagement.Infrastructure.Idempotency;
using ProductManagement.Infrastructure.Security;

namespace ProductManagement.Infrastructure.UnitTests.Edge;

/// <summary>The single-process stores used without Redis expire their entries like the Redis ones do.</summary>
public sealed class InMemoryFallbackTests
{
    private static readonly StoredResponse Ok = new(200, "application/json", "{}", new Dictionary<string, string>());

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task FailedAttemptThrottle_BlocksAfterTheLimitForTheRestOfTheWindow()
    {
        var throttle = new InMemoryFailedAttemptThrottle(
            Options.Create(new FailedAttemptThrottleOptions { MaxFailures = 2, Window = TimeSpan.FromMinutes(1) }),
            _time);

        await throttle.RecordFailureAsync("ip:1", CancellationToken.None);
        (await throttle.GetBlockedForAsync("ip:1", CancellationToken.None)).ShouldBeNull();

        _time.Advance(TimeSpan.FromSeconds(20));
        await throttle.RecordFailureAsync("ip:1", CancellationToken.None);

        (await throttle.GetBlockedForAsync("ip:1", CancellationToken.None)).ShouldBe(TimeSpan.FromSeconds(40));
        (await throttle.GetBlockedForAsync("ip:2", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task FailedAttemptThrottle_ForgetsFailuresOnceTheWindowHasEnded()
    {
        var throttle = new InMemoryFailedAttemptThrottle(
            Options.Create(new FailedAttemptThrottleOptions { MaxFailures = 2, Window = TimeSpan.FromMinutes(1) }),
            _time);
        await throttle.RecordFailureAsync("ip:1", CancellationToken.None);
        await throttle.RecordFailureAsync("ip:1", CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(1));
        (await throttle.GetBlockedForAsync("ip:1", CancellationToken.None)).ShouldBeNull();

        // A new window starts from scratch: one failure is not enough to block again.
        await throttle.RecordFailureAsync("ip:1", CancellationToken.None);
        (await throttle.GetBlockedForAsync("ip:1", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task IdempotencyStore_RecognisesRepeatsUntilTheKeyLifetimeEnds()
    {
        var store = IdempotencyStore();
        (await store.TryBeginAsync("maria:k1", "fp", CancellationToken.None)).Status.ShouldBe(IdempotencyClaimStatus.Started);
        (await store.TryBeginAsync("maria:k1", "fp", CancellationToken.None)).Status.ShouldBe(IdempotencyClaimStatus.InProgress);
        await store.CompleteAsync("maria:k1", "fp", Ok, CancellationToken.None);

        _time.Advance(TimeSpan.FromHours(23));
        var repeat = await store.TryBeginAsync("maria:k1", "fp", CancellationToken.None);
        repeat.Status.ShouldBe(IdempotencyClaimStatus.Completed);
        repeat.Response.ShouldBe(Ok);
        (await store.TryBeginAsync("maria:k1", "other", CancellationToken.None)).Status.ShouldBe(IdempotencyClaimStatus.FingerprintMismatch);

        _time.Advance(TimeSpan.FromHours(1));
        (await store.TryBeginAsync("maria:k1", "other", CancellationToken.None)).Status.ShouldBe(IdempotencyClaimStatus.Started);
    }

    [Fact]
    public async Task IdempotencyStore_ReleasesAnUnfinishedClaimAfterTheInProgressTimeout()
    {
        var store = IdempotencyStore();
        await store.TryBeginAsync("maria:k1", "fp", CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(1));

        (await store.TryBeginAsync("maria:k1", "fp", CancellationToken.None)).Status.ShouldBe(IdempotencyClaimStatus.Started);
    }

    [Fact]
    public async Task IdempotencyStore_AnAbandonedKeyCanBeClaimedAgain()
    {
        var store = IdempotencyStore();
        await store.TryBeginAsync("maria:k1", "fp", CancellationToken.None);

        await store.AbandonAsync("maria:k1", CancellationToken.None);

        (await store.TryBeginAsync("maria:k1", "fp", CancellationToken.None)).Status.ShouldBe(IdempotencyClaimStatus.Started);
    }

    private InMemoryIdempotencyStore IdempotencyStore() =>
        new(
            Options.Create(new IdempotencyOptions
            {
                KeyLifetime = TimeSpan.FromHours(24),
                InProgressTimeout = TimeSpan.FromMinutes(1),
            }),
            _time);
}
