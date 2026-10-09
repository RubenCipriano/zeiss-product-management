using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace ProductManagement.Testing;

/// <summary>
/// Throw-away PostgreSQL and Redis servers in Docker, the same major versions as the Kubernetes deployment. Tests
/// run against the real engines because the behaviour under test (sequences, xmin, row locking, ILIKE, cache
/// invalidation across instances) is engine-specific and in-memory fakes would hide it.
/// </summary>
public sealed class TestInfrastructure : IAsyncDisposable
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("product_management_tests")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:8-alpine").Build();

    public string DatabaseConnectionString => _postgres.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    public Task StartAsync() => Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

    public async Task FlushCacheAsync()
    {
        var result = await _redis.ExecAsync(["redis-cli", "FLUSHALL"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not flush Redis: {result.Stderr}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }
}
