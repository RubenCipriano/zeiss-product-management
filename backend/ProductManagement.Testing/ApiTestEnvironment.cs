using Npgsql;
using Respawn;

namespace ProductManagement.Testing;

/// <summary>
/// Everything an API-level test needs: PostgreSQL and Redis containers, the API hosted in memory with migrations
/// applied, and a fast way to reset data between tests. Start it once per test run; it is expensive (Docker).
/// </summary>
public sealed class ApiTestEnvironment : IAsyncDisposable
{
    private readonly TestInfrastructure _infrastructure;
    private readonly List<ProductManagementApiFactory> _additionalInstances = [];
    private Respawner? _respawner;

    private ApiTestEnvironment(TestInfrastructure infrastructure, ProductManagementApiFactory factory)
    {
        _infrastructure = infrastructure;
        Factory = factory;
    }

    public ProductManagementApiFactory Factory { get; }

    /// <summary>For tests that must change data behind the API's back (e.g. to prove a read came from the cache).</summary>
    public string DatabaseConnectionString => _infrastructure.DatabaseConnectionString;

    public static async Task<ApiTestEnvironment> StartAsync()
    {
        var infrastructure = new TestInfrastructure();
        await infrastructure.StartAsync();

        var factory = new ProductManagementApiFactory(
            infrastructure.DatabaseConnectionString,
            infrastructure.RedisConnectionString,
            applyMigrations: true);
        var environment = new ApiTestEnvironment(infrastructure, factory);

        // Creating a client boots the host, which applies the migrations.
        using (factory.CreateClient())
        {
        }

        await using var connection = new NpgsqlConnection(infrastructure.DatabaseConnectionString);
        await connection.OpenAsync();
        environment._respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],

            // Reference data (seeded by migration) and migration history must survive a reset.
            TablesToIgnore = ["categories", "__ef_migrations_history"],
        });

        return environment;
    }

    /// <summary>A client signed in with the given role (default: manager, who may call every endpoint).</summary>
    public ProductApiClient CreateClient(string? role = TestTokens.Manager) => Authorize(Factory.CreateClient(), role);

    /// <summary>Starts a separate API instance with extra settings (e.g. low rate limits).</summary>
    public ProductApiClient CreateClientForInstanceWith(
        IReadOnlyDictionary<string, string> settings,
        string? role = TestTokens.Manager) =>
        CreateInstance(_infrastructure.RedisConnectionString, role, settings);

    /// <summary>
    /// Starts another, independent API instance sharing the same database and cache (as in a scaled-out deployment).
    /// </summary>
    public ProductApiClient CreateClientForAdditionalInstance() =>
        CreateInstance(_infrastructure.RedisConnectionString, TestTokens.Manager);

    /// <summary>Starts an API instance whose Redis endpoint does not exist, to prove the cache fails open.</summary>
    public ProductApiClient CreateClientWithUnreachableCache() =>
        CreateInstance("127.0.0.1:1,connectTimeout=200", TestTokens.Manager);

    /// <summary>Empties the product table and the cache, so every test starts from the same state.</summary>
    public async Task ResetAsync()
    {
        await using (var connection = new NpgsqlConnection(_infrastructure.DatabaseConnectionString))
        {
            await connection.OpenAsync();
            await _respawner!.ResetAsync(connection);
        }

        await _infrastructure.FlushCacheAsync();
    }

    /// <summary>Empties Redis only, as if it had restarted: cache entries and idempotency claims are gone.</summary>
    public Task FlushCacheAsync() => _infrastructure.FlushCacheAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in _additionalInstances)
        {
            await instance.DisposeAsync();
        }

        await Factory.DisposeAsync();
        await _infrastructure.DisposeAsync();
    }

    private ProductApiClient CreateInstance(
        string redisConnectionString,
        string? role,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        var instance = new ProductManagementApiFactory(
            _infrastructure.DatabaseConnectionString,
            redisConnectionString,
            applyMigrations: false,
            settings);
        _additionalInstances.Add(instance);
        return Authorize(instance.CreateClient(), role);
    }

    private static ProductApiClient Authorize(HttpClient client, string? role)
    {
        if (role is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(role));
        }

        return new ProductApiClient(client);
    }
}
