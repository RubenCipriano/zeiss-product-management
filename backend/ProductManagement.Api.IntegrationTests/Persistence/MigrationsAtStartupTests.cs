using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProductManagement.Infrastructure.Persistence;
using ProductManagement.Testing;
using Testcontainers.PostgreSql;

namespace ProductManagement.Api.IntegrationTests.Persistence;

/// <summary>
/// The API applies its own migrations when it starts (<c>Database:ApplyMigrationsOnStartup</c>), the way it runs in
/// Kubernetes: the database and its two users exist (scripts/create-database.sh in the deploy repository), the schema
/// does not yet. Every test gets a new, empty database on a PostgreSQL server of its own (the shared test database is
/// already migrated).
/// </summary>
public class MigrationsAtStartupTests(MigrationsAtStartupTests.EmptyDatabaseServer server)
    : IClassFixture<MigrationsAtStartupTests.EmptyDatabaseServer>
{
    private const string AppUser = "pm_app";
    private const string AppPassword = "app-password";
    private const string MigratorUser = "pm_migrator";
    private const string MigratorPassword = "migrator-password";

    /// <summary>Which of the two users have a connection open to the database.</summary>
    private const string ConnectedUsers =
        $"SELECT string_agg(DISTINCT usename, ',') FROM pg_stat_activity " +
        $"WHERE datname = current_database() AND usename IN ('{AppUser}', '{MigratorUser}')";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TwoInstancesStartingTogether_OnAnEmptyDatabase_BothStart_AndMigrateItOnce()
    {
        var database = await server.CreateDatabaseAsync("two_instances");
        await using var first = StartingInstance(database);
        await using var second = StartingInstance(database);

        // Creating a client boots the instance, which applies the migrations (and seeds) before it serves requests.
        var clients = await Task.WhenAll(
            Task.Run(first.CreateClient, CancellationToken),
            Task.Run(second.CreateClient, CancellationToken));

        foreach (var client in clients)
        {
            (await client.GetAsync(new Uri("health/ready", UriKind.Relative), CancellationToken)).StatusCode
                .ShouldBe(HttpStatusCode.OK);
            client.Dispose();
        }

        (await server.QueryAsync(database, "SELECT count(*) FROM __ef_migrations_history"))
            .ShouldBe(ProductManagementMigrations.Count.ToString(CultureInfo.InvariantCulture));
        (await server.QueryAsync(database, "SELECT count(*) FROM products")).ShouldBe("24");
    }

    [Fact]
    public async Task Migrations_RunAsTheMigrator_AndRequests_AsTheAppUser()
    {
        var database = await server.CreateDatabaseAsync("least_privilege");
        await using var instance = StartingInstance(database);
        using var client = instance.CreateClient();
        (await client.GetAsync(new Uri("health/ready", UriKind.Relative), CancellationToken)).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        // The schema belongs to the migrator: it created every table, including the migration history.
        (await server.QueryAsync(
            database,
            "SELECT string_agg(DISTINCT tableowner, ',') FROM pg_tables WHERE schemaname = 'public'"))
            .ShouldBe(MigratorUser);

        // The app user may read and write rows (granted by a migration) but not change the schema.
        (await server.QueryAsync(database, $"SELECT has_table_privilege('{AppUser}', 'products', 'INSERT')"))
            .ShouldBe(bool.TrueString);
        (await server.QueryAsync(database, $"SELECT has_schema_privilege('{AppUser}', 'public', 'CREATE')"))
            .ShouldBe(bool.FalseString);

        // Once started, the API is connected as the app user only: the migrator's connections were not pooled and are
        // closed.
        (await server.QueryAsync(database, ConnectedUsers)).ShouldBe(AppUser);
    }

    /// <summary>
    /// An instance configured as in Kubernetes: requests as <c>pm_app</c>; migrations as <c>pm_migrator</c>, whose
    /// user and password are set on their own (the connection string holds neither).
    /// </summary>
    private ProductManagementApiFactory StartingInstance(string database) => new(
        server.ConnectionString(database, AppUser, AppPassword),
        redisConnectionString: null,
        applyMigrations: true,
        new Dictionary<string, string>
        {
            ["Database:MigratorUsername"] = MigratorUser,
            ["Database:MigratorPassword"] = MigratorPassword,
            ["Database:SeedSampleData"] = bool.TrueString,
        });

    /// <summary>The number of migrations in the code: what an up-to-date database has in its history.</summary>
    private static class ProductManagementMigrations
    {
        public static readonly int Count = typeof(ProductManagementDbContext).Assembly.GetTypes()
            .Count(type => type.IsSubclassOf(typeof(Migration)));
    }

    /// <summary>
    /// A PostgreSQL server with the two users of scripts/create-database.sh; each test creates its own database there,
    /// owned by nobody but the administrator, with the same grants as that script.
    /// </summary>
    public class EmptyDatabaseServer : IAsyncLifetime
    {
        private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

        public async ValueTask InitializeAsync()
        {
            await _postgres.StartAsync();
            await ExecuteAsync(
                "postgres",
                $"""
                CREATE ROLE {MigratorUser} LOGIN PASSWORD '{MigratorPassword}';
                CREATE ROLE {AppUser} LOGIN PASSWORD '{AppPassword}';
                """);
        }

        /// <summary>The database and grants of scripts/create-database.sh, without any table.</summary>
        public async Task<string> CreateDatabaseAsync(string name)
        {
            await ExecuteAsync("postgres", $"CREATE DATABASE {name}");
            await ExecuteAsync(
                "postgres",
                $"""
                REVOKE ALL ON DATABASE {name} FROM PUBLIC;
                GRANT CONNECT ON DATABASE {name} TO {MigratorUser}, {AppUser};
                GRANT CREATE ON DATABASE {name} TO {MigratorUser};
                """);
            await ExecuteAsync(
                name,
                $"""
                ALTER SCHEMA public OWNER TO {MigratorUser};
                REVOKE CREATE ON SCHEMA public FROM PUBLIC;
                GRANT USAGE ON SCHEMA public TO {AppUser};
                """);
            return name;
        }

        public string ConnectionString(string database, string? username = null, string? password = null)
        {
            var builder = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database };
            if (username is not null)
            {
                builder.Username = username;
                builder.Password = password;
            }

            return builder.ConnectionString;
        }

        /// <summary>Runs a query as the administrator and returns its single value as text.</summary>
        public async Task<string> QueryAsync(string database, string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString(database));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
        }

        public ValueTask DisposeAsync() => _postgres.DisposeAsync();

        private async Task ExecuteAsync(string database, string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString(database));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
