using ProductManagement.Infrastructure.Persistence;

namespace ProductManagement.UnitTests.Infrastructure.Persistence;

/// <summary>
/// The connection the migrations use at startup: the API's own connection string, with the migrator's user and password
/// when they are configured (Kubernetes), unchanged otherwise (local development, tests).
/// </summary>
public class MigrationConnectionStringTests
{
    private const string AppConnection =
        "Host=postgres.infra.svc.cluster.local;Database=product_management;Username=pm_app;Maximum Pool Size=10";

    [Fact]
    public void WithAMigrator_ConnectsAsTheMigrator_ToTheSameDatabase()
    {
        var connection = DatabaseMigrator.MigrationConnectionString(
            AppConnection,
            new DatabaseOptions { MigratorUsername = "pm_migrator", MigratorPassword = "from-secret" });

        connection.Username.ShouldBe("pm_migrator");
        connection.Password.ShouldBe("from-secret");
        connection.Host.ShouldBe("postgres.infra.svc.cluster.local");
        connection.Database.ShouldBe("product_management");
    }

    [Fact]
    public void WithoutAMigrator_UsesTheConnectionStringsUser()
    {
        var connection = DatabaseMigrator.MigrationConnectionString(AppConnection, new DatabaseOptions());

        connection.Username.ShouldBe("pm_app");
        connection.Password.ShouldBeNull();
    }

    [Fact]
    public void IsNotPooled_AndAllowsLongStatements()
    {
        var connection = DatabaseMigrator.MigrationConnectionString(AppConnection, new DatabaseOptions());

        // Closed as soon as the migration step ends; a long index build is not cut off after the default 30 s.
        connection.Pooling.ShouldBeFalse();
        connection.CommandTimeout.ShouldBe((int)DatabaseMigrator.Timeout.TotalSeconds);
    }
}
