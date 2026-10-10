# 0018 — Autoscaling and testing at scale

**Status:** accepted

> **Update (2026-10-10):** each API replica also holds one connection for its readiness check (`SELECT 1` on its
> own pool, 2 s timeouts), so a saturated request pool cannot fail readiness on every replica at once. At the maximum
> the database sees 8 × (10 + 1) = 88 connections, within `max_connections = 200`. PostgreSQL ran for a while as a
> server outside the cluster ([0021](0021-deployment-layout.md), [0022](0022-database-at-scale.md)); it now runs in
> the cluster again, as a StatefulSet in the shared namespace `infra` with the same settings.

## Context

Two questions the earlier load tests left open: does the system still behave with a realistic amount of data
(not 24 sample products), and does it adapt to load on its own instead of someone running `kubectl scale`? And,
specifically: when many teams change stock at the same time through different replicas, does anything get
duplicated or lost?

## Decision

**Autoscaling.** A `HorizontalPodAutoscaler` owns the API's replica count: 2 to 8, target 60 % of the CPU request.

* Minimum 2: one pod can fail or be replaced while the other serves.
* Maximum 8, and 10 pooled connections per replica: at most 80 connections to PostgreSQL. More API pods than the
  database can serve only move the queue from the API into PostgreSQL, where it slows every query (seen with 6
  replicas × 20 connections, below).
* Fast scale-up (up to 2 pods every 30 s, no stabilisation window) and slow scale-down (3 minutes of lower load,
  then 1 pod a minute): adding capacity late costs errors, removing it late costs only a little CPU.
* The Deployment has no `replicas` field, so `kubectl apply` never fights the autoscaler.
* Needs metrics-server: `deploy/scripts/infra.sh up` installs it where the cluster has none, as on Docker Desktop (k3s and most
  managed clusters ship it).

**Data at scale.** `tools/load-tests/seed-bulk.sh` loads 850,000 products (most of the 900,000 six-digit ids, leaving
room for new ones) with 5 million stock movements, generated in SQL in about two minutes:

* ids from `product_id_seq`, exactly like the API, so later inserts never collide;
* realistic names, categories, prices and dates, and per product 1–11 movements by eight teams (stores, warehouses,
  the online shop, returns);
* consistent by construction: each product's stock is the sum of its movements and the running stock never goes
  below zero. `tools/load-tests/verify-data.sh` checks those invariants (and id uniqueness) in a few seconds.

**The `teams` scenario** (`tools/load-tests/run.sh teams`): virtual users act as teams on the big catalogue. They
browse (including deep pages), search, read products and histories, and change stock, half of the changes on 50
"hot" products that every team touches. 30 % of the stock changes are sent twice with the same `Idempotency-Key`,
one after the other or at the same instant. After the run, the script compares the number of changes the API
reported as applied with the number of rows the ledger gained, and runs the invariant checks.

## Consequences

* The system adapts to load without intervention, within limits that protect the database.
* **The first run at scale failed, which was the point.** Three iterations (details in
  [load-test-results.md](../load-test-results.md)): from 123 requests/s with 11.9 % errors and PostgreSQL
  saturated, to 281 requests/s with 0 errors and p95 13 ms. The fixes were found by measuring, not guessed: lists
  cached in two parts so stock changes do not invalidate them ([0008](0008-redis-cache.md)), an index matching the
  stock-level ordering (184 ms → 0.1 ms), counts cached per list rather than per page, readiness probes that a
  slow database cannot flap, and a smaller pool per replica.
* **Correctness held in every run**, including the failing ones: no duplicated change among 14,579 duplicate
  copies, stock equal to the ledger for every product, unique ids.
* **More replicas are not more capacity when the database is the bottleneck.** In the second run the HPA scaled
  to 6 replicas, and it got worse: more connections queueing on the same 2 cores. Scaling the stateless tier only
  helps once the shared tier has headroom.
* Measurements on the big dataset also showed deep offset pages as a weakness. Page 40,000 took 1.3 s in SQL.
  A late row lookup (find the page's ids through the index, then read those rows) brought it to 0.5 s without
  changing the API. Constant-time deep paging needs keyset pagination (`?after=<name,id>`), listed as a next
  step. The UI never goes that deep, and the cost of a page is cached in Redis afterwards.
* The bulk seed adds data; it does not reset. Resetting means deleting the PostgreSQL volume.
* The HPA reacts to CPU only. The API is CPU-bound in the tests, so that is the right signal here; with a
  different profile (waiting on I/O) a request-rate metric through a metrics adapter would be better.
