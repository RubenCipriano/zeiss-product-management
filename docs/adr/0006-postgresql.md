# 0006 — PostgreSQL as the database

**Status:** accepted; the current setup is the second update below.

> **Update (2026-10-10, second):** PostgreSQL runs in the cluster again, now as shared infrastructure for every
> service ([0021, update](0021-deployment-layout.md#update-2026-10-10-the-infrastructure-moves-into-kubernetes)):
> a StatefulSet with one replica in the namespace `infra` (`infra/postgres.yaml`), its data on a PersistentVolumeClaim,
> the settings inline in a ConfigMap (sized from measurements, [0022](0022-database-at-scale.md)), and a CronJob that
> writes a nightly `pg_dump` of every database to a second volume, kept 7 days. One server for the platform, one
> database per service: `infra/create-database.sh` creates a service's database and two least-privilege users
> (`pm_migrator` owns the schema and runs the migrations; `pm_app`, the API, may only connect and use the schema, with
> a 30 s statement timeout) with random passwords in the Secret `database-credentials`. What `pm_app` may do on the
> tables is granted by the service's own EF Core migration `GrantAppPermissions` (rows and sequences, including
> tables created later). One replica means no standby and no failover, and the dumps give no point-in-time recovery:
> an operator such as **CloudNativePG** (a streaming standby with automatic failover, WAL archiving to object storage
> for point-in-time recovery, scheduled base backups) is the next step for production. Tests still use Testcontainers.
>
> **Update (2026-10-10, first; history):** PostgreSQL no longer runs in Kubernetes. It is a server outside the cluster (locally the
> container `pm-postgres` started by `infra/infra.sh up database`), reached by the API's connection string (first
> through an ExternalName Service `postgres`, removed later the same day: [0021, update](0021-deployment-layout.md#update-2026-10-10)),
> with least-privilege users `pm_migrator` and `pm_app` ([0021](0021-deployment-layout.md)) and settings sized from
> measurements ([0022](0022-database-at-scale.md)). Tests still use Testcontainers.

## Context

The assessment requires EF Core code-first migrations and seeding, and does not prescribe a database. The solution must
run anywhere (macOS/Apple Silicon, Windows, Linux, CI) with one command, and the concurrency guarantees must be
tested against the real engine.

## Decision

PostgreSQL 18 via the Npgsql EF Core provider, run in Kubernetes (StatefulSet with a persistent volume) and in tests (Testcontainers).

It gives this design what it needs:

* sequences with bounds and `NO CYCLE` ([0002](0002-product-id-generation.md));
* a free row version via `xmin` ([0004](0004-optimistic-concurrency.md));
* row-level locking semantics for conditional UPDATEs ([0003](0003-atomic-stock-updates.md));
* `ILIKE` with a **trigram GIN index** (`pg_trgm`) so `%fragment%` name search does not need a full scan as data grows;
* small, multi-arch container images (SQL Server images do not run natively on ARM).

Naming follows PostgreSQL conventions (`snake_case` through `EFCore.NamingConventions`), so hand-written SQL and
database tools feel native.

## Consequences

* Tests use the real database (and, since ADR 0008, real Redis), not EF Core InMemory or SQLite. Those would fake
  exactly the behaviour under test (sequences, locking, `xmin`, `ILIKE`). Integration tests therefore need Docker.
* Moving to SQL Server would mean: a `rowversion` column instead of `xmin`, `LIKE` with a case-insensitive collation
  instead of `ILIKE`, and `NEXT VALUE FOR` in the id generator. Each of these is confined to Infrastructure.
