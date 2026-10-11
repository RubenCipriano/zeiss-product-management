namespace ProductManagement.Infrastructure.Persistence;

/// <summary>Database settings (the <c>Database</c> section of the configuration).</summary>
internal class DatabaseOptions
{
    public const string SectionName = "Database";

    public const string ConnectionStringName = "ProductManagement";

    /// <summary>
    /// Applies pending EF Core migrations when the API starts, before it serves requests
    /// (<see cref="DatabaseMigrator"/>). On by default, in every environment: the API brings its own schema up to
    /// date, so there is no separate migration step to forget. Safe with several replicas starting at once (they take
    /// turns). The integration tests turn it off for extra instances of an already migrated database.
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; init; } = true;

    /// <summary>
    /// Inserts sample products when the database has none. Runs right after the migrations (EF Core seeding hooks).
    /// </summary>
    public bool SeedSampleData { get; init; }

    /// <summary>
    /// The database user that applies the migrations (it owns the schema: <c>pm_migrator</c> in Kubernetes), when it is
    /// not the user of the connection string. Only the migration step connects as this user; requests keep using the
    /// connection string's user (<c>pm_app</c>), which may read and write rows but not change the schema. Not set
    /// (local development, tests): the migrations use the connection string as it is.
    /// </summary>
    public string? MigratorUsername { get; init; }

    /// <summary>
    /// The password of <see cref="MigratorUsername"/> (environment variable <c>Database__MigratorPassword</c>, from a
    /// Kubernetes Secret), so no connection string holds a password.
    /// </summary>
    public string? MigratorPassword { get; init; }
}
