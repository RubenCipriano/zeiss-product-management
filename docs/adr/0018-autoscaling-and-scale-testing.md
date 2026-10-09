# 0018 — Autoscaling and testing at scale

**Status:** accepted

## Context

Two questions the earlier load tests left open: does the system still behave with a realistic amount of data
(not 24 sample products), and does it adapt to load on its own instead of someone running `kubectl scale`? And,
specifically: when many teams change stock at the same time through different replicas, does anything get
duplicated or lost?

## Decision

**Autoscaling.** A `HorizontalPodAutoscaler` owns the API's replica count: 2 to 8, target 60 % of the CPU request.

* Minimum 2: one pod can fail or be replaced while the other serves.
* Maximum 8: set by the database. 8 replicas × 20 pooled connections = 160, below PostgreSQL's `max_connections`
  (300). More API pods than the database can serve only move the queue from the API to PostgreSQL (seen in the
  4-replica runs in [load-test-results.md](../load-test-results.md)).
* Fast scale-up (up to 2 pods every 30 s, no stabilisation window) and slow scale-down (3 minutes of lower load,
  then 1 pod a minute): adding capacity late costs errors, removing it late costs only a little CPU.
* The Deployment has no `replicas` field, so `kubectl apply` never fights the autoscaler.
* Needs metrics-server: `scripts/cluster-addons.sh` installs it on Docker Desktop (MicroK8s has it as an add-on).

**Data at scale.** `load-tests/seed-bulk.sh` loads 850,000 products (most of the 900,000 six-digit ids, leaving
room for new ones) with 5 million stock movements, generated in SQL in about two minutes:

* ids from `product_id_seq`, exactly like the API, so later inserts never collide;
* realistic names, categories, prices and dates, and per product 1–11 movements by eight teams (stores, warehouses,
  the online shop, returns);
* consistent by construction: each product's stock is the sum of its movements and the running stock never goes
  below zero. `load-tests/verify-data.sh` checks those invariants (and id uniqueness) in a few seconds.

**The `teams` scenario** (`load-tests/run.sh teams`): ten consumers act as ten teams on the big catalogue. They
browse (including deep pages), search, read products and histories, and change stock, half of the changes on 50
"hot" products that every team touches. 30 % of the stock changes are sent twice with the same `Idempotency-Key`,
one after the other or at the same instant. After the run, the script compares the number of changes the API
reported as applied with the number of rows the ledger gained, and runs the invariant checks.

## Consequences

* The system adapts to load without intervention, within limits that protect the database.
* Measurements on the big dataset showed one real weakness: deep offset pages. Page 40,000 took 1.3 s in SQL.
  A late row lookup (find the page's ids through the index, then read those rows) brought it to 0.5 s without
  changing the API. Constant-time deep paging needs keyset pagination (`?after=<name,id>`), listed as a next
  step. The UI never goes that deep, and the cost of a page is cached in Redis afterwards.
* The bulk seed adds data; it does not reset. Resetting means deleting the PostgreSQL volume.
* The HPA reacts to CPU only. The API is CPU-bound in the tests, so that is the right signal here; with a
  different profile (waiting on I/O) a request-rate metric through a metrics adapter would be better.
