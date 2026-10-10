# 0008 — Redis read cache with generation-based invalidation

**Status:** accepted

> **Update (2026-10-10):** Redis left the application's namespace: for a while it was a server outside the cluster
> (first through an ExternalName Service `redis`, removed later the same day:
> [0021, update](0021-deployment-layout.md#update-2026-10-10)); it now runs in the cluster's shared namespace `infra`
> (a Deployment, `redis.infra.svc.cluster.local:6379`, [`deploy/infra/redis.yaml`](../../deploy/infra/redis.yaml)), shared by every
> service and reached by the API's connection string. The round trips below are in-cluster round trips again, well
> under a millisecond locally. A Redis command that times out or is
> cancelled is treated like a Redis outage: the cache falls back to the database. The MCP server mentioned below was
> removed ([0014](0014-mcp-server.md)); the read load it stood for (other clients issuing many lookups) is unchanged.

## Context

Reads dominate: the UI polls every 15 s per open tab, the dashboard cards add three list queries per refresh, and AI
assistants (MCP) issue many lookups. With several API replicas, a cache must stay correct across all of them: a write
on replica A must never leave replica B serving stale data.

## Decision

**Cache-aside in Redis, shared by all replicas, invalidated by a catalogue *generation*.**

* `CachingProductQueries` decorates `ProductQueries` (Infrastructure). The application layer still depends on
  `IProductQueries` and does not know a cache exists.
* Every key embeds the current generation, e.g. `product-management:g1791551739:product:100004` or
  `…:g1791551739:products:stock:0:10:p1:s20`.
* Every committed write increments the generation (`INCR`): `UnitOfWork.SaveChangesAsync` when products changed,
  and `ProductRepository.TryAdjustStockAsync` after a successful atomic UPDATE. Old entries are never read again
  and expire through their TTL (5 min items, 1 min lists).
* **Three kinds of generation, because writes differ in what they change** (added after the scale tests in
  [0018](0018-autoscaling-and-scale-testing.md)). Each entry's key embeds the generations it depends on, read in one
  `MGET`:

  | Generation | Moves on when | Versions |
  | ---------- | ------------- | -------- |
  | data | any write | stock-level lists (membership depends on stock) |
  | listing | products created, edited, deleted | the full list and searches: *which* products, in what order, *how many* |
  | product (one per product, 1 h TTL) | that product changes, stock included | its details and its stock history (together with the listing generation) |

  A stock change therefore invalidates one product's entries and the stock-level lists, nothing else. The full list
  and searches are cached in parts: the count (once per list), the page's ids (per page) and the rows, read fresh
  by primary key on every request (~1 ms). On 850,000 products the first two are the expensive part (a count and an
  index walk: 50–170 ms). Under a constant stream of stock changes lists stay cached and still show current stock.
* **Single-flight.** Concurrent misses for the same entry run one query per replica (`SingleFlight`); the others
  wait for its result. Without it, an expired popular entry was recomputed by every request in flight at once:
  882 simultaneous `COUNT(*)` over 850,000 rows, ~2 s each, in one capacity run.
* Listing entries live 10 minutes (they are invalidated explicitly; the TTL only bounds memory); details 5 minutes;
  stock-level lists and histories 1 minute.
* **Race-safe by construction:** readers fetch the generation *before* querying the database. If a write commits in
  between, the reader's possibly-stale result is stored under the old generation, which nobody reads any more. The
  classic "delete key after write" approach does not have this property: a slow reader can re-insert the old value
  after the delete.
* Not-found results are not cached, so a newly created product is visible at once.
* **Fails open:** `AbortOnConnectFail=false`, 500 ms timeouts, every Redis error is caught and logged, and the
  query then goes to PostgreSQL. Readiness reports `Degraded` (HTTP 200), so a Redis outage never takes replicas out
  of rotation.
* The generation key has no TTL and Redis runs with `volatile-lru`, so memory pressure can only evict cache entries,
  never the generation. If Redis loses its data, the generation restarts from the current Unix time (not 0), so an
  old generation number can never come back.

## Consequences

* Product edits invalidate every cached list and detail; a stock change invalidates one product's entries and the
  stock-level lists. Invalidation stays O(1). Under a write-heavy load the hit rate of those drops, but correctness never
  depends on it.
* Before the split, every stock change invalidated every list, so under the teams load test the cache was nearly
  useless and each list request ran `COUNT(*)` over 850,000 rows: PostgreSQL saturated at 2 CPUs
  ([load-test-results.md](../load-test-results.md)).
* A cached page of ids can name a product deleted in the instant before the listing generation moved on; that
  product is then simply missing from the page (19 rows instead of 20) until the next request.
* Two Redis round trips per cached read (generation + value). Both are sub-millisecond in-cluster and cheaper than
  the PostgreSQL query with a join and a count.
* No in-process (L1) cache: it would need a cross-replica invalidation channel (Redis pub/sub backplane) to stay
  correct. If profiling shows Redis latency matters, FusionCache with a Redis backplane is the next step.

Covered by `CacheTests`: cached reads, invalidation visible across two replicas, lists kept across stock changes
while showing the current stock, 404 not cached, fail-open with an unreachable Redis. Every other integration test also runs with the cache enabled.
