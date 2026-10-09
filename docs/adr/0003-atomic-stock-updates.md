# 0003 — Atomic, conditional stock updates

**Status:** accepted

## Context

`decrement-stock` and `add-to-stock` will be called concurrently for the same product (several clients, several API
instances). Two invariants must hold under any interleaving:

1. Stock never goes below zero (no overselling) or above `ProductRules.MaxStockQuantity`.
2. No update is lost: 50 × (+3) and 50 × (−2) always end at +50.

## Decision

Each stock change is a **single conditional UPDATE**. (Since [0016](0016-stock-ledger.md) the same statement also
writes the ledger row, as a data-modifying CTE; the condition is unchanged.)

```sql
UPDATE products
SET    stock_quantity = stock_quantity + @delta, updated_at = @now
WHERE  id = @id
  AND  stock_quantity >= @minCurrent      -- e.g. 3 when removing 3
  AND  stock_quantity <= @maxCurrent;     -- MAX - delta when adding
```

* The **rule lives in the domain**: `Product.StockRangeAllowingAdjustment(delta)` returns the range the current stock
  must be in for the change to be allowed, and that range becomes the `WHERE` clause. The domain still owns the
  business rule, and the database evaluates it atomically.
* PostgreSQL locks the row for the statement and re-evaluates the `WHERE` against the latest committed version. Concurrent
  decrements therefore serialise at the row level and each one sees the result of the previous one.
* A returned row means success. None means the product does not exist or the rule was not met. The service then reads the
  current state once, to tell those cases apart (404 vs 409) and to return the updated product or the available quantity.
* Bounds are computed in C#, so the SQL never computes `stock + delta` and cannot overflow.
* A database `CHECK (stock_quantity BETWEEN 0 AND 1000000)` is the last line of defence for any other write path.
* The follow-up read is a separate statement. A product deleted in the instant between the UPDATE and the read
  reports 404 even though the stock change applied, and the `available` figure in a 409 can already be slightly out of
  date. Both are harmless (the stock invariant itself is never at risk). If exactness ever mattered, the UPDATE could
  return the row itself (`UPDATE … RETURNING`) through raw SQL.

Verified by `ConcurrencyTests` (60 parallel orders for 25 units → exactly 25 succeed, stock ends at 0; mixed +/−
requests lose nothing) and by the BDD scenario *"Simultaneous orders never oversell"*.

## Consequences

* No retries and no spurious conflicts under contention. Throughput on a hot product is bounded only by row-lock duration
  (one short statement).
* Stock changes bypass the load-modify-save path, so `Product` has no `DecreaseStock()` method. This is a deliberate
  deviation from "always go through the aggregate". The rule is still domain-owned and unit-tested
  (`ProductTests.CanAdjustStockBy_*`).
* The UPDATE changes `xmin`, so an editor holding an older ETag gets a 412 instead of silently overwriting the new stock
  with an outdated value (see [0004](0004-optimistic-concurrency.md)).
* **Connection retries are disabled on purpose** (`EnableRetryOnFailure` is not used). If a connection dropped after
  the UPDATE committed but before the acknowledgement arrived, an automatic retry inside the API would apply the
  change twice. Retries belong to the client, with an `Idempotency-Key`, which makes them safe across replicas
  ([0017](0017-idempotency-keys.md)).

## Alternatives considered

| Option                                         | Why not                                                                                     |
| ---------------------------------------------- | ------------------------------------------------------------------------------------------- |
| Load → `product.DecreaseStock()` → save with optimistic concurrency + retry | Correct, but under contention most requests conflict and retry (N requests ⇒ O(N²) work). Clients see failures for requests that would have succeeded. |
| `SELECT ... FOR UPDATE` in a transaction       | Correct, but two round trips while holding a lock, and the transaction must be managed explicitly. |
| Serializable isolation                         | Requires retry loops on serialization failures. This has the same contention cost as optimistic concurrency. |
| In-process lock / `SemaphoreSlim`              | Does not work across multiple API instances.                                               |
