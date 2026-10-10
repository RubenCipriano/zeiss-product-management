# 0022 — The database at scale: indexes, vacuum and the ledger beyond 20 million rows

**Status:** accepted

## Context

The question was "did you add indexes? We have 850,000 products now, but we may want 5 million or more records."
The two tables grow very differently:

* **`products` is capped.** Ids are six digits (`product_id_seq`, 100000–999999, a requirement), so the catalogue
  can never exceed 900,000 rows. It is already at 850,000.
* **`stock_movements` (the ledger, [0016](0016-stock-ledger.md)) grows without bound**: one row per stock change,
  forever. "5 million or more records" is this table.

So the ledger was grown on the real server from 5.55 million to **20 million rows**, consistently (every product's
stock is still the sum of its movements and every `stock_after` the running total: `tools/load-tests/verify-data.sh`
passes). The rows were generated in SQL by `tools/load-tests/data/bulk-ledger.sql`, as +N/−N pairs appended in time order
with a realistic skew: 5 very busy products with ~100,000 movements each, 1,000 busy ones with ~2,000, and the rest
spread over every product. Every query the API runs was then measured with `EXPLAIN (ANALYZE, BUFFERS)` and through
the gateway, at 5.5 and at 20 million rows. The server is PostgreSQL 18 in a container with 1 GiB of memory and a
256 MB buffer cache (measured when PostgreSQL ran as a Docker container on the same machine; it now runs in the
cluster, in the namespace `infra`, with the settings of section 3). The database is 3.4 GB at 20 million movements: ledger 1.6 GB, its two indexes 1.4 GB, products
with indexes ~280 MB. Reads that miss the cache cost 0.1–0.3 ms each on this disk.

## Index inventory

Every index serves a query the API runs; none is unused, none is missing. `DatabaseAtScaleTests` pins the list.

| Index | Serves | Evidence (850,000 products; 20 M movements) |
| --- | --- | --- |
| `pk_products (id)` | product by id, the page rows (`id = ANY(@ids)`), the stock UPDATE, the ledger's FK check | 0.1–0.3 ms |
| `ix_products_name_id (name, id)` | the default list (ids of a page, ordered by name) and search pages for common terms (walks the order, filters) | page 1: 0.1 ms; page 20,000: 110–140 ms (index-only skip, cached in Redis) |
| `ix_products_name` GIN `gin_trgm_ops` | search: `name ILIKE '%term%'` count and pages | rare term 5–7 ms, medium 34 ms, no match 0.2 ms; without it every search is a 100 MB table scan |
| `ix_products_stock_quantity_name_id (stock_quantity, name, id)` | stock-level lists: filter on stock, order by (stock, name, id), count | page 1: 0.04 ms; count 0..10: 4 ms (184 ms with an index on stock alone, [0018](0018-autoscaling-and-scale-testing.md)) |
| `ix_products_category_id (category_id)` | the FK to categories (`ON DELETE RESTRICT` check), and the smallest index on products: the planner counts the full list with a parallel index-only scan of it | `count(*)` 82–115 ms; 35 million tuples read from it in the load tests |
| `pk_stock_movements (id)` | the history's row lookup (below), `max(id)` | 20 lookups per history page |
| `ix_stock_movements_product_id_occurred_at_id (product_id, occurred_at DESC, id DESC)` | history count and page (exactly its order), the FK (`ON DELETE CASCADE` from products: `product_id` leads) | count of 100,000 movements: 10 ms index-only |
| `ix_stock_movements_request_key (request_key) WHERE request_key IS NOT NULL`, unique | exactly-once for keyed requests ([0017](0017-idempotency-keys.md)), "find the product this create request made" | 0.05–1 ms; partial, so only keyed movements pay for it (27 MB) |
| `pk_categories`, `ix_categories_name` unique | category lookups, unique names | 6 rows |

## Decision

### 1. Stock history: late row lookup

A history page was `WHERE product_id = @id ORDER BY occurred_at DESC, id DESC OFFSET @skip LIMIT @take`. The index
returns the rows in order, but `OFFSET` still reads every skipped row **from the table**. A busy product's
movements are scattered across the whole ledger, so each skipped row is another table page. At 20 million rows the
planner even switched to reading all of the product's rows and sorting them on disk. The page's ids are now found
with an index-only scan, and only those rows are read:

```sql
SELECT ... FROM stock_movements AS s
WHERE s.id IN (SELECT s0.id FROM stock_movements AS s0 WHERE s0.product_id = @id
               ORDER BY s0.occurred_at DESC, s0.id DESC LIMIT @take OFFSET @skip)
ORDER BY s.occurred_at DESC, s.id DESC
```

The API contract is unchanged (same parameters, same response). A page past the end skips the query (the count is
already known).

| Last page of a 100,000-movement history (SQL) | First read | Repeated |
| --- | --- | --- |
| `OFFSET` on the index scan (before) | 1.3–6.3 s, 44,000 table pages read, sort spilled to disk | 0.44–1.3 s |
| Late row lookup (now) | 9 ms, ~900 index pages + 20 rows | 8 ms |
| Middle page (50,000 skipped) | 48 ms → 12 ms | |

The old query also evicted ~350 MB of cache every time it ran, slowing everything else.

### 2. Autovacuum per table, so index-only scans stay index-only

Index-only scans (the list count and pages, stock-level counts, history counts and pages) skip the table only for
pages marked all-visible in the visibility map, and only VACUUM sets those marks.

* **`products`**: every stock change rewrites the product's row, and because `stock_quantity` is indexed the update
  can never be HOT (measured: 15 HOT updates out of 11,422). Each one clears its page's mark. The default trigger is
  20% of the table, ~170,000 dead rows. **After just 11,400 stock changes** (no vacuum yet): `count(*)` went from
  64 ms to **1,536 ms** (309,000 table reads), page 20,000 of the list from 122 ms to **2,517 ms**, a stock-level
  count from 56 ms to **512 ms**. After VACUUM: 82, 99 and 36 ms. A VACUUM of products takes 2–10 s. Now:
  `autovacuum_vacuum_scale_factor = 0.005`, `autovacuum_vacuum_threshold = 1000` (≈ 5,300 changes),
  `autovacuum_analyze_scale_factor = 0.02` (the stock-level histogram follows the stock).
* **`stock_movements`** is insert-only, so autovacuum runs on it for inserts: by default after 20% of the table,
  **4 million rows at 20 million**. Right after the load only 28.8% of its pages were all-visible: the count of a
  busy product's history took 50 ms (64,000 table reads) and its deep page 180 ms, against 10 and 9 ms after
  vacuum. The newest rows, which belong to the busiest products, are always the unmarked ones. Now:
  `autovacuum_vacuum_insert_scale_factor = 0`, `autovacuum_vacuum_insert_threshold = 100000`. On an insert-only table
  such a vacuum only visits the new pages (~1,000) and skips index cleanup, and it also freezes early, which makes
  the eventual anti-wraparound vacuum cheap.

Both are model-driven (`HasStorageParameter` in the entity configurations, migration `AutovacuumAtScale`): an
`ALTER TABLE ... SET (...)`, instant, which blocks neither reads nor writes.

### 3. Server settings (`postgresql.conf`, in `deploy/infra/postgres.yaml`)

Applied to the server after these measurements. Each value comes with the evidence for it:

| Setting | Was | Now | Why |
| --- | --- | --- | --- |
| container memory | 1 GiB | **3 GiB** | A 3.4 GB database in 1 GiB is mostly disk reads at 0.1–0.3 ms each: the history page above took 6.3 s cold vs 0.44 s warm, and a history count 250 ms vs 10 ms. During the 20 M load, the ledger's 1 GB index pushed the products working set out of memory. |
| `shared_buffers` | 256 MB | **768 MB** | ~25% of the memory: products with its indexes (~280 MB) plus the hot end of the ledger and its index stay cached. |
| `effective_cache_size` | 768 MB | **2 GB** | What the OS can really cache next to `shared_buffers` in 3 GiB. With 1 GiB, 768 MB overstated it. |
| `maintenance_work_mem` | 128 MB | **256 MB** | Vacuum index passes over the products indexes (~180 MB) and index builds on the ledger (1 GB). |
| `max_wal_size` | 1–2 GB | **4 GB** | The load logged "checkpoints are occurring too frequently" 21 times (down to 4 s apart; 42 requested vs 6 timed checkpoints). One VACUUM of products writes ~160 MB of WAL. |
| `checkpoint_timeout` | 5 min | **15 min** | Fewer full-page images after each checkpoint, with the same crash-recovery bound in practice. |
| `autovacuum_naptime` | 60 s | **30 s** | The per-table thresholds above are reached within seconds under load; checking twice as often keeps the visibility map closer to fresh. |
| `track_io_timing`, `log_autovacuum_min_duration = 1s` | off, 10 min | **on, 1 s** | I/O time shows in plans, and every autovacuum shows in the log (the default hid them all). |
| `work_mem` | 4–8 MB | 8 MB | Enough now: the only query that spilled (the old history sort, 2–6 MB per worker) no longer sorts. The bulk scripts set their own. |

### Measured and rejected

* **`fillfactor` on products.** It leaves room on each page so updates can be HOT. But a stock change modifies an
  indexed column (`stock_quantity`, in the stock-level index), which rules HOT out whatever the free space: 15 of
  11,422 measured updates were HOT. Only edits that change neither name, stock nor category could benefit, and
  those are rare. It would cost 10–30% more table pages on every scan. Making stock changes HOT would mean moving the
  stock out of the indexed row (a separate stock table) or dropping the stock-level index. The first breaks the
  row version used as ETag ([0004](0004-optimistic-concurrency.md)), and the second brings back 184 ms stock-level
  queries. A stock change costs ~1–4 ms in SQL; not worth either.
* **A covering index for the history** (`INCLUDE (quantity_change, stock_after, reason, performed_by)`). It would
  save the 20 row reads of a page (~1–9 ms cold), but it would roughly double the 1 GB index, which every insert
  updates and every skip reads. The late row lookup already removes the expensive part.
* **Keyset (cursor) pagination for the history.** A page after a cursor takes 0.4 ms instead of 8–31 ms, but the
  response's `totalCount` still needs the count (10 ms for 100,000 movements). So the gain per request is at most
  2×, and it needs a new parameter and a client change. Worth adding (`?before=<movement id>`, no total count) only
  when single products reach millions of movements.
* **Keyset for the product lists.** Same trade-off, and page numbers are part of the API and the UI. Deep pages cost
  110–220 ms once and are then cached in Redis ([0008](0008-redis-cache.md)).
* **BRIN on `occurred_at`.** No query filters the ledger by time (the history filters by product). Useful only for
  future reporting by date range, and partitioning (below) would do that better.
* **Estimated counts** (`pg_class.reltuples`) instead of `count(*)`. `totalCount` is exact in the contract and the
  count is cached per list; with the visibility map kept fresh it is 82–115 ms for 850,000 products.
* **Higher statistics targets.** `n_distinct` of `stock_movements.product_id` is estimated at 148,000 (really
  ~850,000), but no plan depends on it: the history is an index scan per product either way, and the busy
  products are in the most-common-values list. ANALYZE stays cheap.
* **GIN `fastupdate = off`** on the trigram index: every non-HOT stock change also adds the name's trigrams to the
  GIN index's pending list, which is flushed in a burst. No latency spike from it showed in the measurements. Keep
  the default and revisit if the slow-query log shows one.

## The ledger beyond 50–100 million rows: partitioning (plan, not implemented)

At 20 million rows the ledger works with indexes alone: the history's cost depends on the product's own number of
movements, not on the size of the table. Past ~50–100 million rows (5–10 GB of table, 4–7 GB of indexes) three
costs grow with the table: vacuum and freeze of the whole table, index size versus memory (every insert touches a
random leaf of the 1 GB+ product index), and the lack of a way to retire old data. The plan:

1. **Range-partition `stock_movements` by `occurred_at`, monthly.** Each partition has its own small, mostly-cold
   indexes. The current month's index is the one inserts touch, and it fits in memory. A history query reads the
   newest partitions first: PostgreSQL reads range partitions in order for `ORDER BY occurred_at DESC` and stops at
   the `LIMIT`. Old months become read-only, frozen once, and can be detached and archived if the business allows a
   retention period. Partitions are created ahead of time (pg_partman or a scheduled job), with a default partition
   as a safety net.
2. **Keys change.** A primary key or unique index on a partitioned table must include the partition key, so it
   becomes `PRIMARY KEY (id, occurred_at)`. The identity sequence still hands out unique ids. Running totals stay
   ordered by `id`. The FK to products (`ON DELETE CASCADE`) works across partitions; a product delete probes every
   partition's index.
3. **Exactly-once needs a different home.** Today a unique index on `request_key` guarantees that a replayed stock
   change fails and rolls back ([0017](0017-idempotency-keys.md)). On a partitioned table it would have to be
   `UNIQUE (request_key, occurred_at)`, which guarantees nothing: a replay carries a new `occurred_at`. The
   alternative is a small, **non-partitioned `stock_requests (request_key PRIMARY KEY, movement_id, product_id,
   created_at)`** table written by the same statement as the change (a third step in the data-modifying CTE). A
   duplicate key still fails the whole statement, stock update included, exactly as now. Only keyed requests land in
   it, and rows can be deleted after the idempotency window (days, far longer than any client retry), so it stays
   small. "Find the product this create request made" reads it instead of the ledger.
4. **Migration without downtime**: create the partitioned table. Attach the existing table as the partition for
   everything before the switch: add a `CHECK (occurred_at < cutover)` as `NOT VALID`, validate it without blocking
   writes, then `ATTACH PARTITION`. Move the request keys into `stock_requests`, then rename in one short
   transaction. EF Core needs no model change for partitions; the migration is raw SQL like the concurrent index
   builds.
5. **Before partitioning, the cheaper step**: the history's total count is the one part that stays linear in a
   product's movements. If single products reach millions of movements, keep a `movement_count` on the product row
   (incremented by the same UPDATE that changes the stock, which rewrites the row anyway) and add the keyset
   parameter above.

## Consequences

* Deep history pages of the busiest products went from seconds to milliseconds, with no API change: last page of
  100,944 movements 1,645 ms → 19–22 ms in SQL (first read 1.3–6.3 s → 9 ms); through the gateway, on a Redis miss,
  630 ms → 30–99 ms.
* A VACUUM of the 20-million-row ledger after the change scanned 187 pages in 0.04 s: with the insert threshold, each
  autovacuum only visits what is new.
* Index-only scans stay index-only between vacuums. The price is more frequent, short autovacuums on two tables.
* The capped products table needs no further indexes. The ledger needs none until partitioning, and the plan for
  that is written down, including how exactly-once survives it.
* `tools/load-tests/seed-bulk.sh` can grow the ledger (`LEDGER_ROWS=20000000`), and `seed-bulk.sh`, `verify-data.sh` and
  `run.sh` talk to PostgreSQL and Redis directly (`kubectl -n infra exec` into `statefulset/postgres` /
  `deployment/redis` by default, `PSQL` / `REDIS_CLI` to target others).
* Numbers before and after, including through the gateway, are in [load-test-results.md](../load-test-results.md).
