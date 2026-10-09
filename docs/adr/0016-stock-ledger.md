# 0016 — Stock ledger written in the same statement as the stock change

**Status:** accepted

## Context

A stock quantity alone answers "how many are there?" but not "why?", "who changed it?" or "was this request applied
twice?". With many teams changing stock at the same time (stores, warehouses, the online shop, the MCP server),
those questions come up at once: to audit, to reconcile, and to prove that concurrency is handled.

## Decision

Every stock change is recorded in an append-only **`stock_movements`** table:

| Column            | Meaning |
| ----------------- | ------- |
| `id`              | `bigint` identity: also the order of movements for a product. |
| `product_id`      | FK to `products`, `ON DELETE CASCADE` (the history goes with the product). |
| `quantity_change` | Signed: `+10` delivery, `-3` sale. |
| `stock_after`     | The product's stock right after this movement (`CHECK >= 0`). |
| `reason`          | `InitialStock` (create), `StockAdded`, `StockRemoved`, `Correction` (an edit that changed the quantity). |
| `performed_by`    | The token's subject (`sub`): which consumer made the change. |
| `occurred_at`     | When. |

* **Same statement, not same transaction.** The stock update and the ledger insert are one SQL statement, a
  data-modifying CTE:

  ```sql
  WITH updated AS (
      UPDATE products SET stock_quantity = stock_quantity + @delta, updated_at = @now
      WHERE id = @id AND stock_quantity BETWEEN @min AND @max
      RETURNING id, stock_quantity)
  INSERT INTO stock_movements (product_id, quantity_change, stock_after, reason, performed_by, occurred_at)
  SELECT id, @delta, stock_quantity, @reason, @performedBy, @now FROM updated
  RETURNING stock_after;
  ```

  Atomicity comes for free (one statement), it stays one round trip, and `stock_after` is the value the row lock
  produced, so the running totals are exact under any concurrency. The response still comes from a follow-up read
  of the product ([0003](0003-atomic-stock-updates.md)), which is what the endpoint returns.
* Create and update go through EF Core: `AddMovement` adds the movement to the same unit of work, so it is saved in
  the same transaction as the product.
* `GET /api/products/{id}/stock-movements` pages through the history, newest first, served by the index
  `(product_id, occurred_at DESC, id DESC)`. Readers see it in the UI's *Stock history* dialog.
* The migration backfills one `InitialStock` movement per existing product, so the invariant holds from day one.

**Invariants** (checked by `load-tests/data/verify.sql` and by integration tests):

1. `products.stock_quantity = SUM(stock_movements.quantity_change)` for every product.
2. Each `stock_after` equals the running total of the movements before it (by `id`).
3. Every applied request has exactly one movement: a retried request adds none ([0017](0017-idempotency-keys.md)).

## Consequences

* An audit trail and a reconciliation check for free, and a way to *prove* that no change was lost or doubled
  after a load test.
* Writes are a little heavier (one extra row and index entry per change). Measured: no visible change in write
  latency in the load tests.
* The table grows with every movement (5 million rows for 850,000 products in the bulk test data: ~600 MB with
  indexes). At production volumes it would be partitioned by month and old partitions archived.
* `stock_quantity` stays on `products` as a materialised total. Computing it from the ledger on every read would
  be correct but slow; keeping both in one statement keeps them in step.
* Raw SQL for this one statement (EF Core cannot express a data-modifying CTE). It is parameterised
  (`SqlQuery` with an interpolated string becomes parameters) and covered by integration tests against real
  PostgreSQL.

## Alternatives considered

| Option | Why not |
| ------ | ------- |
| Insert the movement in a second statement inside a transaction | Two round trips and an explicit transaction around the hottest path, for the same result. |
| A database trigger on `products` | Hides behaviour from the code and cannot know *who* made the change or *why* without session variables. |
| Event sourcing (ledger only, stock derived) | Correct but a different architecture: every read would need a projection. |
