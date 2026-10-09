# 0008 — Redis read cache with generation-based invalidation

**Status:** accepted

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

* Any write invalidates every cached list. That is acceptable because reads far outnumber writes and invalidation
  is O(1). Under a write-heavy load the hit rate drops, but correctness never depends on it.
* Two Redis round trips per cached read (generation + value). Both are sub-millisecond in-cluster and cheaper than
  the PostgreSQL query with a join and a count.
* No in-process (L1) cache: it would need a cross-replica invalidation channel (Redis pub/sub backplane) to stay
  correct. If profiling shows Redis latency matters, FusionCache with a Redis backplane is the next step.

Covered by `CacheTests`: cached reads, invalidation visible across two replicas, 404 not cached, fail-open with an
unreachable Redis. Every other integration test also runs with the cache enabled.
