namespace ProductManagement.Infrastructure.Persistence;

internal sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public const string ConnectionStringName = "ProductManagement";

    /// <summary>
    /// Applies pending EF Core migrations when the API starts. Convenient for local development and the in-memory test
    /// host. In Kubernetes migrations run once per deployment instead, in a Job (the <c>migrate</c> command).
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; init; }

    /// <summary>
    /// Inserts sample products when the database has none. Runs as part of migration (EF Core seeding hooks).
    /// </summary>
    public bool SeedSampleData { get; init; }
}
