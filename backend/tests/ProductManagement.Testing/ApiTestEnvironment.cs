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

    public ProductApiClient CreateClient() => new(Factory.CreateClient());

    /// <summary>
    /// Starts another, independent API instance sharing the same database and cache (as in a scaled-out deployment).
    /// </summary>
    public ProductApiClient CreateClientForAdditionalInstance() =>
        CreateInstance(_infrastructure.RedisConnectionString);

    /// <summary>Starts an API instance whose Redis endpoint does not exist, to prove the cache fails open.</summary>
    public ProductApiClient CreateClientWithUnreachableCache() =>
        CreateInstance("127.0.0.1:1,connectTimeout=200");

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

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in _additionalInstances)
        {
            await instance.DisposeAsync();
        }

        await Factory.DisposeAsync();
        await _infrastructure.DisposeAsync();
    }

    private ProductApiClient CreateInstance(string redisConnectionString)
    {
        var instance = new ProductManagementApiFactory(
            _infrastructure.DatabaseConnectionString,
            redisConnectionString,
            applyMigrations: false);
        _additionalInstances.Add(instance);
        return new ProductApiClient(instance.CreateClient());
    }
}
