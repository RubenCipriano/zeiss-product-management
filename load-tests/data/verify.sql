-- Data invariants. Every count in the result must be 0. Run with load-tests/verify-data.sh before and after a
-- load test: concurrent requests from many replicas must never break them.

\timing on
SELECT
  -- Six-digit ids are unique (the primary key guarantees it; counted to show it).
  (SELECT count(*) - count(DISTINCT id) FROM products) AS duplicate_product_ids,
  (SELECT count(*) FROM products WHERE id NOT BETWEEN 100000 AND 999999) AS ids_out_of_range,
  -- A product's stock is the sum of its ledger...
  (SELECT count(*)
     FROM products AS p
     JOIN (SELECT product_id, sum(quantity_change) AS total FROM stock_movements GROUP BY product_id) AS m
       ON m.product_id = p.id
    WHERE p.stock_quantity <> m.total) AS stock_differs_from_ledger_sum,
  -- ...and every product has a ledger.
  (SELECT count(*) FROM products AS p
    WHERE NOT EXISTS (SELECT 1 FROM stock_movements AS m WHERE m.product_id = p.id)) AS products_without_ledger,
  -- Each movement's stock_after is the running total of the movements before it (no lost or doubled update).
  (SELECT count(*) FROM (
     SELECT stock_after,
            sum(quantity_change) OVER (PARTITION BY product_id ORDER BY id) AS running
       FROM stock_movements) AS l
    WHERE stock_after <> running) AS broken_running_totals,
  (SELECT count(*) FROM products WHERE stock_quantity < 0) AS negative_stock;

SELECT (SELECT count(*) FROM products) AS products,
       (SELECT count(*) FROM stock_movements) AS stock_movements,
       pg_size_pretty(pg_database_size(current_database())) AS database_size;
