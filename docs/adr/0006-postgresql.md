# 0006 — PostgreSQL as the database

**Status:** accepted

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
