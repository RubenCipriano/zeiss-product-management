using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProductManagement.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// What the API's database user may do: read and write rows and draw ids, on today's tables and sequences and on
    /// every one a later migration creates. Nothing else: no DDL, so a bug or an injected query cannot change the
    /// schema. The users themselves (pm_migrator owns the schema and runs this; pm_app is the API) are created by the
    /// platform, not by a migration: <c>scripts/create-database.sh product-management product_management pm_app
    /// pm_migrator</c>.
    /// </summary>
    /// <remarks>
    /// A migration is compiled, so it cannot read the role name from configuration: it uses the well-known name
    /// <c>pm_app</c> (the user in the API's connection string), and does nothing where that role does not exist,
    /// such as the integration tests' Testcontainers database or a local database used as its superuser, where
    /// everything connects as the owner anyway. Run as the schema owner, so <c>ALTER DEFAULT PRIVILEGES</c> covers the
    /// tables and sequences that later migrations create.
    /// </remarks>
    public partial class GrantAppPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'pm_app') THEN
                    GRANT USAGE ON SCHEMA public TO pm_app;
                    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO pm_app;
                    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO pm_app;
                    ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO pm_app;
                    ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO pm_app;
                  END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'pm_app') THEN
                    ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE USAGE, SELECT ON SEQUENCES FROM pm_app;
                    ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM pm_app;
                    REVOKE USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public FROM pm_app;
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public FROM pm_app;
                  END IF;
                END
                $$;
                """);
        }
    }
}
