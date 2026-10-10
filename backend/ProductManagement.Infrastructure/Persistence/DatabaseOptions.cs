namespace ProductManagement.Infrastructure.Persistence;

/// <summary>Database settings (the <c>Database</c> section of the configuration).</summary>
internal class DatabaseOptions
{
    public const string SectionName = "Database";

    public const string ConnectionStringName = "ProductManagement";

    /// <summary>
    /// Applies pending EF Core migrations when the API starts. Convenient for local development and the in-memory test
    /// host. Deployments instead run migrations once, before the rollout: the CI/CD pipeline (or the deploy
    /// repository's scripts/deploy.sh locally) runs the API image's <c>migrate</c> command with the migrator's
    /// database credentials.
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; init; }

    /// <summary>
    /// Inserts sample products when the database has none. Runs as part of migration (EF Core seeding hooks).
    /// </summary>
    public bool SeedSampleData { get; init; }
}
