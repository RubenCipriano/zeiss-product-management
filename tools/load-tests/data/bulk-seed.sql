-- Bulk test data: a large catalogue with a consistent stock ledger, for load and scalability tests.
--
--   tools/load-tests/seed-bulk.sh                    # 850,000 products, ~5 million stock movements
--   PRODUCTS=100000 tools/load-tests/seed-bulk.sh    # smaller
--
-- What it creates, per product:
--   * a realistic name (line + type + variant + model), category, price and creation date in the last two years;
--   * 1 to 11 stock movements in date order: the initial stock, then deliveries, sales and corrections made by
--     different teams (stores, warehouses, the online shop);
--   * a stock quantity that is exactly the sum of its movements, and a ledger whose running stock never goes below 0.
-- So the invariants the API maintains also hold for the generated data, and the verification queries in
-- tools/load-tests/data/verify.sql can be run before and after a test.
--
-- Ids come from product_id_seq, like the API's own inserts, so products created later through the API never
-- collide with these. The sequence ends at 999999: keep PRODUCTS below the remaining ids to leave room for tests.
-- Psql variable `products` sets the count. setseed makes runs reproducible.

\set ON_ERROR_STOP on
\if :{?products}
\else
  \set products 850000
\endif
\timing on

SELECT setseed(0.42);
SET work_mem = '128MB';
SET maintenance_work_mem = '256MB';

SELECT 999999 - CASE WHEN is_called THEN last_value ELSE last_value - 1 END AS ids_left,
       999999 - CASE WHEN is_called THEN last_value ELSE last_value - 1 END >= :products AS enough_ids
FROM product_id_seq \gset
\if :enough_ids
\else
  \echo 'Only' :ids_left 'product ids left in product_id_seq; seed fewer products'
  \quit 1
\endif

BEGIN;

-- Name parts per category (ids 1-6 from the migration's seed data).
CREATE TEMP TABLE seed_names (category_id int PRIMARY KEY, types text[], variants text[]) ON COMMIT DROP;
INSERT INTO seed_names VALUES
  (1, '{Single Vision Lens,Progressive Lens,Office Lens,Bifocal Lens,Driving Lens}',
      '{1.50,1.60,1.67,1.74,Polycarbonate,Photochromic,BlueGuard,DuraVision}'),
  (2, '{Titanium Frame,Acetate Frame,Rimless Frame,Half-Rim Frame,Kids Frame}',
      '{Black,Tortoise,Gunmetal,Gold,Crystal,Navy,Burgundy}'),
  (3, '{Aviator Sunglasses,Wayfarer Sunglasses,Sport Sunglasses,Round Sunglasses,Polarised Sunglasses}',
      '{Grey,Brown,Green,Mirror Blue,Gradient}'),
  (4, '{Daily Contact Lenses,Monthly Contact Lenses,Toric Contact Lenses,Multifocal Contact Lenses}',
      '{6 pack,30 pack,90 pack}'),
  (5, '{Cleaning Spray,Multi-Purpose Solution,Microfibre Cloth,Lens Wipes,Saline Solution}',
      '{60 ml,100 ml,250 ml,360 ml,Pack of 50}'),
  (6, '{Hard Case,Soft Pouch,Glasses Chain,Anti-Fog Kit,Nose Pads}',
      '{Black,Blue,Red,Silver,Brown}');

CREATE TEMP TABLE seed_products ON COMMIT DROP AS
SELECT nextval('product_id_seq')::int AS id,
       c.category_id,
       format('%s %s %s M-%s',
              (ARRAY['Aurora','Vista','Clarity','Horizon','Summit','Nova','Pulse','Zenith','Lumen','Atlas','Orbit','Prism'])
                [1 + floor(random() * 12)::int],
              n.types[1 + floor(random() * array_length(n.types, 1))::int],
              n.variants[1 + floor(random() * array_length(n.variants, 1))::int],
              100 + floor(random() * 900)::int) AS name,
       CASE WHEN random() < 0.7 THEN 'Generated for load testing.' END AS description,
       round((5 + random() * 595)::numeric, 2) AS price,
       now() - random() * interval '730 days' AS created_at,
       1 + floor(random() * 11)::int AS movement_count
FROM generate_series(1, :products) AS g
CROSS JOIN LATERAL (SELECT 1 + floor(random() * 6)::int + g * 0 AS category_id) AS c -- per row, see seed_moves
JOIN seed_names AS n USING (category_id);

-- Raw movements: step 1 is the initial stock, later steps are sales (60%), deliveries (35%) and corrections (5%),
-- spread evenly between the product's creation and now.
CREATE TEMP TABLE seed_moves ON COMMIT DROP AS
SELECT p.id AS product_id,
       s.step,
       CASE WHEN s.step = 1 THEN 'InitialStock'
            WHEN r.kind < 0.60 THEN 'StockRemoved'
            WHEN r.kind < 0.95 THEN 'StockAdded'
            ELSE 'Correction' END AS reason,
       CASE WHEN s.step = 1 THEN 20 + floor(r.amount * 380)::int
            WHEN r.kind < 0.60 THEN -(1 + floor(r.amount * 25)::int)
            WHEN r.kind < 0.95 THEN 10 + floor(r.amount * 140)::int
            ELSE (1 + floor(r.amount * 10)::int) * CASE WHEN r.sign < 0.5 THEN -1 ELSE 1 END END AS delta,
       CASE WHEN s.step = 1 THEN 'bulk-seed'
            ELSE (ARRAY['store-lisbon','store-porto','store-munich','store-berlin','warehouse-jena',
                        'warehouse-aalen','online-shop','returns-desk'])[1 + floor(r.team * 8)::int] END AS performed_by,
       p.created_at + (now() - p.created_at) * (s.step - 1) / p.movement_count AS occurred_at
FROM seed_products AS p
CROSS JOIN LATERAL generate_series(1, p.movement_count) AS s(step)
-- `+ s.step * 0` correlates the subquery with the row, so random() is evaluated per movement and not once.
CROSS JOIN LATERAL (SELECT random() + s.step * 0 AS kind, random() AS amount, random() AS team, random() AS sign) AS r;

-- Running stock per product. Where sales would take it below zero, the initial stock is raised by the deficit,
-- which shifts the whole running total up: every stock_after is then >= 0 and the lowest point is exactly 0.
CREATE TEMP TABLE seed_ledger ON COMMIT DROP AS
SELECT product_id, step, reason, performed_by, occurred_at,
       delta + CASE WHEN step = 1 THEN shift ELSE 0 END AS quantity_change,
       running + shift AS stock_after,
       step = movement_count AS is_last
FROM (
  SELECT *, greatest(0, -min(running) OVER (PARTITION BY product_id)) AS shift
  FROM (
    SELECT m.*, p.movement_count, sum(m.delta) OVER (PARTITION BY m.product_id ORDER BY m.step) AS running
    FROM seed_moves AS m
    JOIN seed_products AS p ON p.id = m.product_id
  ) AS with_running
) AS with_shift;

INSERT INTO products (id, name, description, price, stock_quantity, category_id, created_at, updated_at)
SELECT p.id, p.name, p.description, p.price, l.stock_after, p.category_id, p.created_at, l.occurred_at
FROM seed_products AS p
JOIN seed_ledger AS l ON l.product_id = p.id AND l.is_last
ORDER BY p.id;

INSERT INTO stock_movements (product_id, quantity_change, stock_after, reason, performed_by, occurred_at)
SELECT product_id, quantity_change, stock_after, reason, performed_by, occurred_at
FROM seed_ledger
ORDER BY product_id, step;

COMMIT;

ANALYZE products;
ANALYZE stock_movements;

SELECT (SELECT count(*) FROM products) AS products,
       (SELECT count(*) FROM stock_movements) AS stock_movements,
       pg_size_pretty(pg_database_size(current_database())) AS database_size;
