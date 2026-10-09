# 0004 — Optimistic concurrency with `xmin`, ETag and If-Match

**Status:** accepted

## Context

`PUT /api/products/{id}` replaces every field, including `stockQuantity`. A user who opens the edit form, waits, then
saves would otherwise overwrite changes made in between: another user's rename, or stock sold in the meantime
(a classic *lost update*).

## Decision

* `Product.Version` maps to PostgreSQL's system column **`xmin`** (`IsRowVersion()` in the Npgsql provider). PostgreSQL
  changes it on every row update, including the atomic stock UPDATEs. It needs no extra column and no trigger.
* Every product representation carries the version in the body (`version`) and, for single-resource responses, as a
  strong **`ETag`** header (`"1234"`).
* `PUT` and `DELETE` accept **`If-Match`** (RFC 9110 §13.1.1):
  * the header matches → the change applies;
  * the header doesn't match → **412 Precondition Failed** (`product.version_mismatch`);
  * the header is malformed (weak tag, list, not a number) → 400;
  * no header (or `*`) → the change applies to the current state (last writer wins), but still within a concurrency
    check between our own read and write. If the row changes between them, the result is **409**
    (`product.concurrent_modification`).
* EF Core uses the loaded `xmin` as the original value in the UPDATE's `WHERE`, so a race between the version check
  and the write is also caught (`DbUpdateConcurrencyException` → `ConcurrencyConflictException` → 412/409).

The Angular client always sends `If-Match`. On 412 it explains the conflict and offers **Reload latest**.

## Consequences

* Lost updates are impossible for clients that send `If-Match`, at zero storage cost.
* `If-Match` is optional to keep the API easy to try (Swagger, curl). A stricter deployment could return
  **428 Precondition Required** when it is missing. That is a one-line change in `ProductsController`.
* `xmin` is PostgreSQL-specific. On another database this becomes a `rowversion`/`timestamp` column. Only the EF
  configuration changes.
* `xmin` is a 32-bit transaction id and can wrap around after ~4 billion transactions. An ETag collision would need a
  row to remain unchanged for that long and then match exactly, which is acceptable for this use.
