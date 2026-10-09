using Npgsql;
using Respawn;

namespace ProductManagement.Testing;

/// <summary>
/// Everything an API-level test needs: a PostgreSQL container, the API hosted in memory with migrations applied,
/// and a fast way to reset data between tests. Start it once per test run; it is expensive (Docker).
/// </summary>
public sealed class ApiTestEnvironment : IAsyncDisposable
{
    private readonly PostgresDatabase _database;
    private readonly List<ProductManagementApiFactory> _additionalInstances = [];
    private Respawner? _respawner;

    private ApiTestEnvironment(PostgresDatabase database, ProductManagementApiFactory factory)
    {
        _database = database;
        Factory = factory;
    }

    public ProductManagementApiFactory Factory { get; }

    public string ConnectionString => _database.ConnectionString;

    public static async Task<ApiTestEnvironment> StartAsync()
    {
        var database = new PostgresDatabase();
        await database.StartAsync();

        var factory = new ProductManagementApiFactory(database.ConnectionString, applyMigrations: true);
        var environment = new ApiTestEnvironment(database, factory);

        // Creating a client boots the host, which applies the migrations.
        using (factory.CreateClient())
        {
        }

        await using var connection = new NpgsqlConnection(database.ConnectionString);
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
    /// Starts another, independent API instance that shares the same database (as in a scaled-out deployment).
    /// </summary>
    public ProductApiClient CreateClientForAdditionalInstance()
    {
        var instance = new ProductManagementApiFactory(_database.ConnectionString, applyMigrations: false);
        _additionalInstances.Add(instance);
        return new ProductApiClient(instance.CreateClient());
    }

    public async Task ResetDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await _respawner!.ResetAsync(connection);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in _additionalInstances)
        {
            await instance.DisposeAsync();
        }

        await Factory.DisposeAsync();
        await _database.DisposeAsync();
    }
}
