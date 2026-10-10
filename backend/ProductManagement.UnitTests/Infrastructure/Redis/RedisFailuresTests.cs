using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ProductManagement.Infrastructure.Redis;
using ProductManagement.Infrastructure.Security;
using StackExchange.Redis;

namespace ProductManagement.UnitTests.Infrastructure.Redis;

/// <summary>Every way a Redis command can fail at runtime degrades the feature; none reaches the client as a 500.</summary>
public class RedisFailuresTests
{
    public static TheoryData<Exception> RedisCommandFailures =>
    [
        new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "no connection", null, CommandStatus.Unknown),
        new RedisTimeoutException(CommandFlags.None, "timed out after 500 ms", CommandStatus.Sent),
        new TaskCanceledException(), // a command the client library abandoned
        new ObjectDisposedException("ConnectionMultiplexer"),
    ];

    [Theory]
    [MemberData(nameof(RedisCommandFailures))]
    public void RuntimeFailuresOfACommand_AreTransient(Exception exception)
    {
        RedisFailures.IsTransient(exception).ShouldBeTrue();
    }

    [Fact]
    public void ProgrammingErrors_AreNot()
    {
        RedisFailures.IsTransient(new InvalidCastException()).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(RedisCommandFailures))]
    public async Task SharedRateLimiter_FallsBackToTheLocalLimit_WhenTheCommandFails(Exception exception)
    {
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(Arg.Any<LuaScript>(), Arg.Any<object?>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromException<RedisResult>(exception));
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        var limiter = new RedisDistributedRateLimiter(
            redis,
            Options.Create(new RedisOptions()),
            NullLogger<RedisDistributedRateLimiter>.Instance);

        var decision = await limiter.TryTakeAsync("writes:user:maria", 1, 2, TestContext.Current.CancellationToken);

        decision.ShouldBeNull();
    }
}
