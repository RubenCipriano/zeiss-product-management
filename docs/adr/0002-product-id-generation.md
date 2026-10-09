# 0002 — Product identifiers from a PostgreSQL sequence

**Status:** accepted

## Context

Requirement: *"The product identifier should be auto-generated as a unique 6-digit number. Even when using multiple
instances at the same time, no duplicate IDs should be generated."*

So the identifier must be:

* exactly six digits (100000–999999, i.e. 900,000 values);
* unique across any number of concurrently running API instances;
* never reused, even after a delete (otherwise an old reference would silently point at a new product).

## Decision

A dedicated database sequence, created by the EF Core migration:

```sql
CREATE SEQUENCE product_id_seq AS integer START 100000 MINVALUE 100000 MAXVALUE 999999 NO CYCLE;
```

`PostgresProductIdGenerator` (behind the `IProductIdGenerator` port) calls `nextval('product_id_seq')` before the
product is created, so the domain object is constructed with its final, valid identifier.

Why this is safe across instances: `nextval()` is **atomic and non-transactional**. The database serialises it,
so two callers never receive the same value, whichever process, connection or transaction they come from.
A value handed out in a transaction that later rolls back is simply skipped (a gap); it is never reissued.
Defence in depth: the primary key enforces uniqueness and a check constraint enforces the six-digit range.

This is verified by `ConcurrencyTests.IdGeneration_AcrossMultipleApiInstances_NeverProducesDuplicates`. Two independent
API hosts (separate DI containers and connection pools) create 150 products concurrently against one database.

## Consequences

* One extra, very cheap round trip per create.
* **Gaps** are possible, e.g. when a save fails after its id was assigned. Validation and the category check run
  *before* an id is requested, so invalid requests never consume ids.
* **Capacity** is 900,000 products, then `nextval` fails with SQLSTATE `2200H`. The generator turns that into a
  clear error. Widening the range later is a one-line migration (`ALTER SEQUENCE ... MAXVALUE`) once the business
  accepts seven digits.
* **Predictable** ids (sequential). That is fine for an internal catalogue, but they should not be treated as secrets.
  If enumeration ever matters, a format-preserving permutation (e.g. a Feistel network over the sequence value) gives
  non-sequential-looking six-digit ids that stay unique, with no extra storage.

## Alternatives considered

| Option                                   | Why not                                                                                       |
| ---------------------------------------- | --------------------------------------------------------------------------------------------- |
| Random 6-digit number + retry on conflict | Birthday problem: collision probability grows quickly in a 900k space (≈50% after ~1,100 rows). Needs retries and still races without a unique constraint. |
| `MAX(id) + 1`                            | Races between instances unless the table is locked, and deleted ids get reused.                |
| GUID / Snowflake / ULID                  | Not six digits.                                                                               |
| EF Core HiLo                             | Uses a sequence underneath but reserves blocks per instance. Restarts burn whole blocks, which is wasteful in a 900k space. |
| Identity column                          | Works, but the id only exists after the INSERT. Using an explicit sequence makes the "distributed-safe id generation" an explicit, testable component, as the assessment asks. |
