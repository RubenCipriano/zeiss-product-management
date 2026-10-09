using Testcontainers.PostgreSql;

namespace ProductManagement.Testing;

/// <summary>
/// A throw-away PostgreSQL server in Docker, the same major version as docker-compose.yml. Tests run against the
/// real database engine because the behaviour under test (sequences, xmin, row locking, ILIKE) is engine-specific
/// and an in-memory provider would fake it.
/// </summary>
public sealed class PostgresDatabase : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("product_management_tests")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task StartAsync() => _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
