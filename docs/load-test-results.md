# Load test results

Runs made on 2026-10-09 with the k6 scenarios in [`backend/load-tests/`](../backend/load-tests), executed **inside** the cluster
(`backend/load-tests/run.sh`), so they include the gateway, the API, PostgreSQL, Redis and token validation.

**Environment:** MicroK8s 1.34 in a single Multipass VM with **4 vCPU / 6 GB RAM** on an Apple Silicon laptop. k6
itself (about 0.5 CPU at peak), the gateway, PostgreSQL and every API replica share those 4 vCPU, so the absolute
numbers are a lower bound for a real cluster. Comparisons between runs, and the correctness checks, are the
point.

**Accounts.** The runs below signed in as ten dedicated load-test consumers. Those were removed: only the three demo
accounts (viewer, clerk, manager) exist now, so `run.sh` lifts the API's per-user limits for the duration of every
scenario except `rate-limit` (which checks the real limits) and restores them afterwards. The tests measure capacity
with a few accounts; they no longer simulate many distinct users.

## Docker Desktop cluster (final setup)

Re-run on Docker Desktop's single-node Kubernetes, with tokens from the API's own endpoint and the gateway's 1 s
microcache for API reads:

| Run | API replicas | Requests | Errors | p50 | p95 | Result |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| smoke | 2 | 227 | 0 | 3.1 ms | 35 ms | ✅ |
| load, 150 req/s | 2 | 21,616 | 0 | 1.9 ms | **3.6 ms** | ✅ SLO; **38 % of product GETs answered by the nginx microcache** |
| stock-consistency | 2 | 320 (20 were retries after `429`) | 0 unexpected | 5.7 ms | 105 ms | ✅ exactly 100 sold, 200 × 409, final stock 0 |

The first consistency run counted 5 `429` responses as unexpected: 30 simultaneous orders exceed the per-user write
limit, and the API throttled them as designed. The scenario now behaves like a well-behaved client: it waits for
`Retry-After` and retries, so all 300 orders are processed and correctness is checked on every one of them.

## Many teams on 850,000 products (Docker Desktop, autoscaling)

The `teams` scenario ([ADR 0018](adr/0018-autoscaling-and-scale-testing.md)) on the bulk dataset
(`backend/load-tests/seed-bulk.sh`: **850,024 products, 5.1 million stock movements, 1 GB**). Ten consumers act as ten
teams: browsing (20 % of list views go up to page 2,000), searching, reading products and histories, and changing
stock (half on 50 hot products all teams touch). **30 % of the stock changes are sent twice with the same
`Idempotency-Key`**, one after the other or at the same instant. The API autoscales (HPA, 2–8 replicas).
Docker Desktop, 8 CPUs shared by everything; PostgreSQL limited to 2 CPUs. Target 300 iterations/s for 4 minutes.

Three runs, each fixing what the previous one revealed:

| Run | Requests/s | Errors | p50 | p95 | PostgreSQL CPU | API replicas | Thresholds |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 1. As before the scale work | 123 | 11.9 % | 840 ms | 15.9 s | **2.0 / 2 (saturated)** | 2 (no metrics yet) | ❌ |
| 2. + cache split, tolerant probes | 211 | 6.6 % | 143 ms | 8.5 s | **2.0 / 2 (saturated)** | 2 → 4 → 6 | ❌ |
| 3. + stock-level index, count cache, pool 10 | **281** | **0 %** | **2.1 ms** | **13.4 ms** | 0.3–0.4 / 2 | 3 → 5 → 7 → down | ✅ all |

Run 3 per endpoint (p95): list 16 ms, product 5 ms, search 5 ms, stock level 17 ms, history 12 ms, stock change
10 ms; p99 over everything 63 ms.

**What each run found:**

1. **Every stock change invalidated every cached list.** With ~20 changes per second the Redis cache was useless,
   and each list request ran `COUNT(*)` over 850,000 rows. PostgreSQL saturated; the API's readiness probe (which
   checks the database, with Kubernetes' default 1 s timeout) failed, so replicas flapped in and out of the load
   balancer. → Lists are now cached in two parts: which products and how many (under a *listing* generation that
   stock changes do not move) and the rows (read fresh by primary key every time). The readiness probe waits 3 s
   and needs 30 s of failures ([ADR 0008](adr/0008-redis-cache.md)).
2. **Still saturated; the autoscaler made it worse.** The HPA scaled the API to 6 replicas on the start-up CPU
   spike, but the API was mostly *waiting*: 6 × 20 connections on 2 database cores queued even primary-key reads
   for seconds. Sampling `pg_stat_activity` and `EXPLAIN ANALYZE` showed the real cost: the stock-level page took
   **184 ms** (an index on `stock_quantity` alone, then the rows read and sorted by name) at 15 requests/s, about
   2.8 CPU-seconds per second on its own; and each new deep page recounted all products. → An index on
   `(stock_quantity, name, id)` (**0.1 ms**, built `CONCURRENTLY`), counts cached once per list instead of per page,
   and 10 connections per replica (8 × 10 = 80 at most).
3. **Within targets with headroom.** PostgreSQL at 15–20 % of its CPU; API pods at 70–250 m each.

**Correctness, every run** (`run.sh` compares the changes k6 saw applied with the rows the ledger gained, then
runs `backend/load-tests/verify-data.sh`):

| Run | Applied (k6) | Outcome unknown to k6 | Ledger rows added | Replayed copies | Simultaneous copies refused (in progress) | Invariants |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| 1 | 6,561 | (not tracked) | 6,562 | 1,879 | 682 | ✅ all 0 |
| 2 | 12,801 | 89 | 12,808 | 3,846 | 1,256 | ✅ all 0 |
| 3 | **17,213** | **0** | **17,213** | **5,188** | **1,728** | ✅ all 0 |

* **No duplicates, ever:** across the three runs, 10,913 replayed copies and 3,666 simultaneous copies, often on a
  different replica than the original, and no change was applied twice. Every replay returned exactly the original
  response (checked on each one).
* **Nothing lost:** stock equals the sum of its ledger for all 850,024 products; every running total is
  consistent; no negative stock; 850,024 unique ids.
* **The one-row difference in run 1** is a request that timed out on the client after the server had applied it.
  The client never learned the outcome. That is exactly the case `Idempotency-Key` exists for: from run 2 on, k6
  retries unknown outcomes with the same key, like a correct client, and the count became exact (run 2's 7 extra
  rows are among its 89 unknown outcomes, which were requests that exhausted their retries while the database was
  saturated).

**Autoscaling, run 3** (from `kubectl get hpa` every 15 s): 3 replicas at the start (still scaling down from the
previous run), 5 after the first minute of ramp, 7 a minute later. Average CPU then fell to ~40 % (new pods use
extra CPU while .NET compiles hot paths), and after the 3-minute stabilisation window the HPA removed one pod per
minute back towards 2.

## Capacity: how far one laptop goes (Docker Desktop, 8 CPUs for everything)

Same cluster, rate limits lifted for the run (they protect against one abusive client, not capacity), the load
generator inside the same 8 CPUs. Two profiles:

* **`teams` with `PEAK=`** (worst case): the many-teams mix, 25 % writes (30 % of them duplicated), reads spread
  *uniformly* over 850,000 products, so caches rarely help; a staircase in five steps.
* **`browse`** (typical): read-only catalogue browsing with a hot set (first 20 pages, 500 most viewed products,
  popular searches, the low-stock view).

| Run | Change since the previous one | Requests | Average req/s | Errors | Bottleneck observed |
| --- | --- | ---: | ---: | ---: | --- |
| teams → 4,000 | (after the fixes above) | 314,533 | 925 | 8.8 % | PostgreSQL at 2/2 CPUs; ~500 changes applied twice (see below) |
| teams → 4,000 | database guard for idempotency, `pg_stat_statements` | 475,271 | 778 | 18.9 % | PostgreSQL: cache **stampedes** (882 × `COUNT(*)` at ~2 s) and lock waits |
| teams → 6,000 | single-flight, per-product cache generations, PostgreSQL 4 CPUs | **827,512** | **2,500** | **0 %** | the node: 7.7 / 8 CPUs (k6 1.2); PostgreSQL 1.8 / 4 |
| browse → 12,000 | — | **1,575,631** | **5,216** | **0 %** (p95 124 ms) | the node: 6.5 / 8 CPUs; 65 % answered by nginx |
| browse, constant 10,000 | — | 351,680 | **5,470 sustained** | 0.01 % | the node: 7.6 / 8 CPUs; k6 could not start 242,720 requests |

**What the capacity runs found (and fixed):**

1. **Duplicates under overload.** At 1,000+ req/s clients timed out and the gateway closed their connections; the
   API saw the cancellation after the stock change had committed, released the Redis claim, and the retry ran
   again: ~500 duplicates in 52,000. Fixed in the database: the ledger row carries the request's key under a
   unique index, in the same statement as the stock change ([ADR 0017](adr/0017-idempotency-keys.md)). Every run
   since: exact.
2. **Cache stampedes.** `pg_stat_statements` showed 882 `COUNT(*)` over 850,000 rows at ~2 s each: when a popular
   entry expired, every request in flight recomputed it. Fixed with single-flight per replica and longer TTLs for
   explicitly invalidated entries ([ADR 0008](adr/0008-redis-cache.md)).
3. **Over-broad invalidation.** A stock change invalidated every product's details. Now only that product's.
4. **Redis sizing.** At 2,500 req/s Redis was OOMKilled (192 MiB limit, 128 MB `maxmemory`) holding a day of
   idempotency responses. Now 256 MB / 384 MiB and 1 hour of responses; the database guard covers the rest. The
   restart also proved the guard: the ledger stayed exact.

**Reading the numbers.** On this machine every component, *and the load generator*, shares 8 CPUs; at the top of
each run the node itself is saturated, not a single tier. Each API pod restarts at the start of a capacity run
(lifting the limits changes its environment), so the first step also measures cold starts. Beyond ~5,500 req/s
the next levers are:

* **a shared microcache at the gateway.** Today a cached read is reused only for the same token (10 consumers =
  10 copies of every entry; 48–65 % hit rate). If the gateway verified tokens itself (ES256: the API signs, the
  gateway checks with the public key), entries could be shared per role and most reads would never reach the API;
* **more nodes**: the API and gateway already scale horizontally (HPA); the database would get its own machine and
  read replicas for lists and searches.

## Five digits: tokens verified at the gateway, microcache shared per role

[ADR 0020](adr/0020-edge-token-verification.md): the API signs with ES256 and publishes its public key; the gateway
verifies every token (nginx njs) and keys the microcache by role instead of by token. Same laptop, same 850,000
products, load generator included.

| Run | Requests | Sustained req/s | Errors | p50 | p95 | Answered by nginx |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| browse, constant 10,000/s (before: per-token cache) | 351,680 | 5,470 | 0.01 % | 99.9 ms | 793 ms | 48 % |
| browse, constant 10,000/s | 596,352 | **9,712** | **0 %** | **0.3 ms** | **7 ms** | 94 % |
| browse, constant 20,000/s | 1,078,380 | **17,359** | **0 %** | 10.7 ms | 79 ms | 92 % |
| browse, constant 20,000/s, shared rate limits (final) | 1,139,168 | **18,177** | **0 %** | 1.2 ms | 47 ms | 96 % |
| teams, 300/s (correctness, final) | 92,859 | 281 | 0.01 % | 1.3 ms | **4.3 ms** | – |
| teams, staircase to 6,000 (worst case, final) | 781,268 | 2,365 average | **0 %** | 40 ms | 2.6 s at the top step | – |

* The worst case (25 % writes, reads spread uniformly over 850,000 products) does not benefit from a shared cache:
  it stays at ~2,400 requests/s, bound by the machine's CPU, with 135,076 changes → exactly 135,076 ledger rows.
  Reaching five digits for *that* mix needs more machines (the API and gateway already scale out; the database would
  get its own node and read replicas).
* At 20,000/s the load generator is the limit: k6 used 2.3 CPUs and could not start ~5–10 % of the requests; the
  node still had CPU to spare.
* **teams, final:** 16,880 changes applied → exactly 16,880 ledger rows; 4,991 replays and 1,753 simultaneous copies,
  none applied twice; all invariants 0. p95 4.3 ms (13.4 ms before, with the per-token cache).
* **Found on the way: per-replica rate limits grow with the autoscaler.** With 6 API replicas the `rate-limit`
  scenario (one user at 300 requests/s) saw no 429 at all: 6 × 50/s. The per-user buckets are now in Redis, shared by
  all replicas: the same run gives ~72/s accepted (50/s + the burst) and 4,566 × 429 ([ADR 0010](adr/0010-rate-limiting.md)).

## MicroK8s runs (capacity investigation)

The runs below were made earlier on MicroK8s (Multipass VM, 4 vCPU / 6 GB), before the API issued its own tokens.

## The database at 20 million ledger rows (dedicated PostgreSQL server)

The catalogue is capped at 900,000 products (six-digit ids), so the table that grows is the stock ledger. It was
grown from 5.55 to **20 million movements** on the PostgreSQL server outside the cluster
(`PRODUCTS=0 LEDGER_ROWS=20000000 backend/load-tests/seed-bulk.sh`: +N/−N pairs in time order, 5 products with ~100,000
movements each, 1,000 with ~2,000; 14.4 million rows in 19.5 minutes). `backend/load-tests/verify-data.sh` passes on the
result: 20,010,066 movements, every invariant 0, in 53 s. Decisions and the full index inventory:
[ADR 0022](adr/0022-database-at-scale.md).

**SQL** (`EXPLAIN ANALYZE`, ms, repeated run; the server's cache was cold for the busy product's history at first):

| Query | 5.5 M movements | 20 M, before | 20 M, after |
| --- | ---: | ---: | ---: |
| Full list count (850,000 products) | 82 | 115 | 46–59 |
| List page 20,000 (ids) | 111 | 137 | 65–99 |
| Search "lens" count (312,000 matches) | 164 | 325 | 210–297 |
| Stock-level 0..10, count / page 1 | 3.8 / 0.06 | 4.3 / 0.03 | 2.7–3.6 / 0.04 |
| Product by id | 0.1 | 0.3 | 0.1–0.2 |
| History count, busiest product (3,468 → 100,944 movements) | 0.9 | 10 warm, 206–259 cold | 10–24 |
| History page 1, busiest product | 0.07 | 0.6 | 0.2 |
| **History last page, busiest product** | 8.8 | **1,288–6,284** (44,000 table pages, sort on disk) | **8–22** |
| Stock change (UPDATE + ledger INSERT) | 1.2 | 3.8–10 | 1.2–3.0 |

"Before" runs on the 1 GiB server, "after" with the [ADR 0022](adr/0022-database-at-scale.md) changes (late row
lookup for the history, per-table autovacuum) and the server resized to 3 GiB with a 768 MB buffer cache. A busy
product's deep history page is the one query that grew with the ledger, and the only one that needed a change.
Everything else depends on the capped products table.

**Visibility map** (why the autovacuum settings): 11,400 stock changes without a vacuum (the default waits for
~170,000) took the full-list count from 64 ms to **1,536 ms**, list page 20,000 from 122 to **2,517 ms** and a
stock-level count from 56 to **512 ms**, all from table reads that index-only scans normally skip. VACUUM brought
them back to 82, 99 and 36 ms.

**Through the gateway** (median of 5–7 requests; "Redis miss" = cache generations bumped before each request,
`Cache-Control: no-cache` past the microcache; "Redis hit" = the same request repeated):

| Request | 5.5 M, miss / hit | 20 M before (1 GiB), miss / hit | 20 M after (3 GiB), miss / hit |
| --- | ---: | ---: | ---: |
| List page 1 | 36 / 8 | 32 / 7 | 40 / 10 |
| List last page (42,502) | 105 / 24 | 86 / 8 | 100–130 / 12 |
| Search "lens" page 1 | 181 / 12 | 144 / 7 | 242–296 / 11–21 |
| Stock-level 0..10 page 1 | 12 / 7 | 11 / 5 | 19 / 6 |
| Product by id | 8 / 6 | 7 / 5 | 7 / 5 |
| History of the busiest product, page 1 | 24 / 49 | 20 / 7 | 19 / 5 |
| **History of the busiest product, last page** | 138 / 39 (174 pages) | **630** / 6 (5,048 pages) | **30–99** / 6 (5,049 pages) |
| Add / remove stock | 52 / 59 | 20 / 18 | 13–30 / 15–29 |

The gateway numbers are noisy (the cluster shared the laptop with other work, and the "after" runs came right after a
server restart and a rollout). The SQL table is the reliable comparison. The miss on the busiest product's last
history page went from 630 ms to 30–99 ms, and the first request for such a page from 1.3–6.3 s in SQL to under
10 ms.

## Summary

| Run | API replicas | Target | Requests | Errors | p50 | p95 | Result |
| --- | ---: | --- | ---: | ---: | ---: | ---: | --- |
| smoke | 2 | 1 user, every endpoint | 220 | 0 | 3.6 ms | 79 ms | ✅ all checks |
| stock-consistency | 2 | 300 concurrent orders, 100 units | 300 | 0 unexpected | 11 ms | 179 ms | ✅ exactly 100 sold, 200 × 409, final stock 0 |
| load | 2 | 150 req/s plateau, realistic mix | 21,616 | 0 | 2.1 ms | **3.2 ms** | ✅ SLO p95 < 300 ms |
| stress (→ 800 req/s) | 2 | steps 200 → 800 req/s | 105,591 | 0 | 1.3 ms | 3.1 ms | ✅ no degradation up to 800 req/s |
| stress (→ 2000 req/s) | 2 | steps 500 → 2000 req/s | 255,558 | 0 | 3.4 ms | 941 ms | knee at ~1,000 req/s |
| stress (→ 2000 req/s) | 4 | same | 235,971 | **282 (0.11 %)** | 6.2 ms | 1.35 s | worse: database saturated |
| stress (→ 2000 req/s), tuned | 4 | same, PostgreSQL 2 CPU, pool 20 | 253,331 | **0** | 5.0 ms | 779 ms | fixed the errors; node CPU-bound |
| spike | 4 | 20 → 500 req/s in 10 s, read traffic | 21,450 | 0 | 0.9 ms | 1.5 ms | ✅ absorbed by the cache |
| rate-limit | 4 | one user at ~300 req/s | 5,907 | 1,590 × `429` (expected) | – | – | ✅ every 429 has `Retry-After`, the rest served |

Rate limits were raised for the stress runs (see below). Under normal limits, the first 2000 req/s run levelled off
at exactly ~1,000 req/s with 24 % `429` responses and no 5xx: 10 simulated users × 50 req/s × 2 replicas. That is
the per-user limit working as designed, not a capacity limit.

## What the numbers say

1. **Comfortably within SLO at the expected load.** At 150 req/s (a busy back-office catalogue), p95 is 3 ms with
   two replicas using ~0.12 CPU each. The Redis cache answers most reads.

2. **Correct under concurrency, across replicas.** 300 simultaneous orders for 100 units through two API replicas:
   exactly 100 succeeded, 200 were told "insufficient stock", and the stock ended at 0. Atomic conditional UPDATEs
   (ADR 0003) give this without locks in the application.

3. **Scaling the API alone did not raise throughput here, because the bottleneck was PostgreSQL.** In both 2000 req/s
   runs PostgreSQL sat at its 1-CPU limit. Ten percent of the stress traffic is stock writes, each of which moves the
   cache to a new generation (ADR 0008), so most reads at that rate go to the database. Doubling the API replicas
   doubled the connections competing for the same saturated database: connection-open timeouts surfaced as 282
   errors.

4. **Tuning the database tier fixed it.** Giving PostgreSQL 2 CPUs and capping the pool at 20 connections per
   replica (so `replicas × pool` stays within what the database can serve, and excess requests queue in the app
   instead of thrashing PostgreSQL) removed every error and cut p95 from 1.35 s to 0.78 s at the same load. What
   remains is the 4-vCPU node itself: API, database and k6 together use all of it.

5. **Limits protect without hurting legitimate traffic.** A single user hammering at ~300 req/s was throttled (27 %
   `429` with `Retry-After`), and everything that got through succeeded. About 215 req/s were allowed with four
   replicas, matching the documented "per-user limit × replicas" behaviour of the in-memory limiter (ADR 0010).
   A sudden read spike to 500 req/s caused no errors: with no writes, the cache serves almost everything.

6. **Scaling mechanics work as designed.** After `kubectl scale deployment/api --replicas=4` (backend only), the
   gateway spread 200 consecutive requests exactly 50/50/50/50 over the four pods within seconds, with no change or
   restart on the frontend side (headless Service + `resolve`, ADR 0012).

## Where the next capacity comes from

In order of expected impact for a write-heavy peak:

* **More nodes / dedicated database resources.** The single biggest factor; everything above shares 4 vCPU.
* **Keyset pagination** for constant-time deep pages (offset pages stay linear in the page number).
* **PgBouncer** in transaction mode in front of PostgreSQL, sized to the database, for many replicas.
* **Read replicas** for list/search queries, the primary only for writes.

## Reproduce

```bash
backend/load-tests/run.sh smoke
backend/load-tests/run.sh stock-consistency
RATE=150 backend/load-tests/run.sh load
PEAK=2000 backend/load-tests/run.sh stress
kubectl -n product-management patch hpa api -p '{"spec":{"minReplicas":4}}'   # the HPA owns the count
PEAK=2000 backend/load-tests/run.sh stress
backend/load-tests/run.sh rate-limit

infra/cluster-addons.sh                   # metrics-server, for the autoscaler
backend/load-tests/seed-bulk.sh                   # 850,000 products, 5 million movements (~2 minutes)
PRODUCTS=0 LEDGER_ROWS=20000000 backend/load-tests/seed-bulk.sh   # grow the ledger to 20 million movements (~20 minutes)
RATE=300 backend/load-tests/run.sh teams          # many teams + duplicate retries, then verifies the data
PEAK=6000 K6_CPU=3 backend/load-tests/run.sh teams  # the same as a capacity staircase (limits lifted)
PEAK=12000 K6_CPU=4 backend/load-tests/run.sh browse   # read capacity with a hot set
RATE=10000 K6_CPU=4 backend/load-tests/run.sh browse   # hold a constant rate for a minute
kubectl -n product-management get hpa api -w   # in another terminal: watch it scale
```
