-- Grows the stock ledger (stock_movements) to a target size without changing any stock, for scalability tests of
-- the table that grows without bound. The catalogue is capped at 900,000 products (six-digit ids); the ledger is not.
--
--   LEDGER_ROWS=20000000 tools/load-tests/seed-bulk.sh      # after (or instead of) the product seed: PRODUCTS=0
--
-- What it appends, in time order, after every existing movement:
--   * pairs of movements by 'bulk-seed-2': a delivery of N units (StockAdded) immediately followed by the removal of
--     the same N units (StockRemoved), so every product keeps its stock and the ledger sum stays equal to it;
--   * stock_after is the running total (stock + N, then stock again): never below 0, consistent with tools/load-tests/data/
--     verify.sql (running totals by id) and with the history order (occurred_at increases with id);
--   * a skewed distribution, like a real ledger: 5 very busy products get ~3.4% of the rows (~100,000 movements each
--     at 20 million), 1,000 busy ones ~14% (~2,000 each), and the rest is spread over every product.
--
-- It runs in batches (one transaction each, `batch_rows` rows) so autovacuum and checkpoints keep up and an
-- interruption loses one batch at most; run it again to continue. Each batch holds a SHARE lock on products for its
-- duration: stock changes and creates through the API wait instead of interleaving with the batch, so the running
-- totals stay exact and every movement the API writes afterwards is newer than the batch's. Measured on the local
-- server (1 GiB container): 14.4 million rows in ~19 minutes, 1-4 minutes per batch of 1,000,000 rows, i.e. writes
-- stalled for up to minutes. The default of 200,000 rows per batch keeps each stall to seconds; run it when the
-- API is quiet anyway (the random index inserts evict the working set of a small server).

\set ON_ERROR_STOP on
\if :{?ledger_rows}
\else
  \set ledger_rows 20000000
\endif
\if :{?batch_rows}
\else
  \set batch_rows 200000
\endif
\timing on

SELECT setseed(0.43);
SET work_mem = '256MB';

-- Every product, numbered: n 1-5 are the very busy products (the longest histories already), 6-1005 the busy ones
-- (a deterministic spread over the id range), then everything else.
CREATE TEMP TABLE ledger_products AS
WITH very_busy AS (
  SELECT product_id AS id FROM stock_movements GROUP BY product_id ORDER BY count(*) DESC, product_id LIMIT 5
), busy AS (
  SELECT id FROM products WHERE id NOT IN (SELECT id FROM very_busy) ORDER BY hashint4(id), id LIMIT 1000
)
SELECT row_number() OVER (ORDER BY tier, hashint4(id), id)::int AS n, id
FROM (
  SELECT 1 AS tier, id FROM very_busy
  UNION ALL SELECT 2, id FROM busy
  UNION ALL SELECT 3, id FROM products WHERE id NOT IN (SELECT id FROM very_busy UNION ALL SELECT id FROM busy)
) AS tiers;
CREATE UNIQUE INDEX ON ledger_products (n);
ANALYZE ledger_products;

SELECT set_config('ledger.target', :'ledger_rows', false) AS target,
       set_config('ledger.batch_rows', :'batch_rows', false) AS batch_rows,
       (SELECT count(*) FROM stock_movements) AS before;

DO $$
DECLARE
  target bigint := current_setting('ledger.target')::bigint;
  batch_rows bigint := current_setting('ledger.batch_rows')::bigint;
  product_count int := (SELECT count(*) FROM ledger_products);
  remaining bigint := target - (SELECT count(*) FROM stock_movements);
  pairs bigint;
  last_id bigint;
  window_start timestamptz;
  window_end timestamptz;
  done bigint := 0;
BEGIN
  IF product_count < 1005 THEN
    RAISE EXCEPTION 'bulk-ledger needs at least 1,005 products, found %', product_count;
  END IF;

  LOOP
    -- No API stock change runs while the batch is written (they wait on this lock): the stock read below is final.
    LOCK TABLE products IN SHARE MODE;
    pairs := least(batch_rows, remaining) / 2;
    EXIT WHEN pairs <= 0;

    -- The batch's movements happen between the newest existing one and now. The first batch scans the whole table
    -- (no index on occurred_at); later ones only look at what was added since the previous batch.
    IF last_id IS NULL THEN
      SELECT max(occurred_at), max(id) INTO window_start, last_id FROM stock_movements;
    ELSE
      SELECT greatest(window_end, max(occurred_at)) INTO window_start FROM stock_movements WHERE id > last_id;
      window_start := coalesce(window_start, window_end);
    END IF;
    window_start := window_start + interval '1 millisecond';
    window_end := greatest(clock_timestamp(), window_start + interval '1 second');

    INSERT INTO stock_movements (product_id, quantity_change, stock_after, reason, performed_by, occurred_at)
    SELECT pick.id, m.change, product.stock_quantity + m.above_stock, m.reason, 'bulk-seed-2',
           window_start + (window_end - window_start) * ((2 * pair.ord + m.k)::float8 / (2 * pairs + 2))
    FROM (
      SELECT g AS ord,
             CASE WHEN r.tier < 0.034 THEN 1 + floor(r.u * 5)::int
                  WHEN r.tier < 0.172 THEN 6 + floor(r.u * 1000)::int
                  ELSE 1006 + floor(r.u * (product_count - 1005))::int END AS n,
             1 + floor(r.amount * 50)::int AS quantity
      FROM generate_series(1, pairs) AS g
      -- `+ g * 0` correlates the subquery with the row, so random() is evaluated per pair and not once.
      CROSS JOIN LATERAL (SELECT random() + g * 0 AS tier, random() AS u, random() AS amount) AS r
    ) AS pair
    JOIN ledger_products AS pick ON pick.n = least(pair.n, product_count)
    JOIN products AS product ON product.id = pick.id
    CROSS JOIN LATERAL (VALUES (0, pair.quantity, pair.quantity, 'StockAdded'),
                               (1, -pair.quantity, 0, 'StockRemoved')) AS m(k, change, above_stock, reason)
    ORDER BY pair.ord, m.k;

    SELECT max(id) INTO last_id FROM stock_movements;
    done := done + 2 * pairs;
    remaining := remaining - 2 * pairs;
    COMMIT;
    RAISE NOTICE '% rows appended (% in this batch, up to id %)', done, 2 * pairs, last_id;
  END LOOP;
END
$$;

ANALYZE stock_movements;

SELECT count(*) AS stock_movements,
       count(*) FILTER (WHERE performed_by = 'bulk-seed-2') AS appended_by_bulk_ledger,
       pg_size_pretty(pg_total_relation_size('stock_movements')) AS ledger_size,
       pg_size_pretty(pg_database_size(current_database())) AS database_size
FROM stock_movements;
