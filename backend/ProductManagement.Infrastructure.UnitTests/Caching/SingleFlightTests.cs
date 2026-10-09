using ProductManagement.Infrastructure.Caching;

namespace ProductManagement.Infrastructure.UnitTests.Caching;

public sealed class SingleFlightTests
{
    private readonly SingleFlight _singleFlight = new();

    [Fact]
    public async Task ConcurrentCallsForTheSameKey_ShareOneLoad()
    {
        var loads = 0;
        var release = new TaskCompletionSource();
        async Task<string?> Load()
        {
            Interlocked.Increment(ref loads);
            await release.Task;
            return "value";
        }

        var calls = Enumerable.Range(0, 10).Select(_ => _singleFlight.RunAsync("key", Load)).ToList();
        release.SetResult();
        var results = await Task.WhenAll(calls);

        loads.ShouldBe(1);
        results.ShouldAllBe(result => result == "value");
    }

    [Fact]
    public async Task DifferentKeys_LoadIndependently()
    {
        var loads = 0;
        Task<string?> Load()
        {
            Interlocked.Increment(ref loads);
            return Task.FromResult<string?>("value");
        }

        await Task.WhenAll(_singleFlight.RunAsync("a", Load), _singleFlight.RunAsync("b", Load));

        loads.ShouldBe(2);
    }

    [Fact]
    public async Task WhenTheLeaderFails_AWaiterLoadsForItself()
    {
        var leaderStarted = new TaskCompletionSource();
        var failLeader = new TaskCompletionSource();
        var leader = _singleFlight.RunAsync<string>("key", async () =>
        {
            leaderStarted.SetResult();
            await failLeader.Task;
            throw new OperationCanceledException("the leader's request was cancelled");
        });
        await leaderStarted.Task;

        var waiter = _singleFlight.RunAsync<string>("key", () => Task.FromResult<string?>("loaded by the waiter"));
        failLeader.SetResult();

        await Should.ThrowAsync<OperationCanceledException>(leader);
        (await waiter).ShouldBe("loaded by the waiter");
    }

    [Fact]
    public async Task AfterALoadCompletes_TheNextCallLoadsAgain()
    {
        var loads = 0;
        Task<string?> Load() => Task.FromResult<string?>($"value {Interlocked.Increment(ref loads)}");

        (await _singleFlight.RunAsync("key", Load)).ShouldBe("value 1");
        (await _singleFlight.RunAsync("key", Load)).ShouldBe("value 2");
    }
}
