namespace ProductManagement.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public const string ConnectionStringName = "ProductManagement";

    /// <summary>
    /// Applies pending EF Core migrations when the API starts. Convenient for local development and the Docker
    /// Compose setup. In production, prefer running a migration bundle as a separate deployment step.
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; init; }

    /// <summary>
    /// Inserts sample products when the database has none. Runs as part of migration (EF Core seeding hooks).
    /// </summary>
    public bool SeedSampleData { get; init; }
}
