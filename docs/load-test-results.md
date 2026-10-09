# Load test results

Runs made on 2026-10-09 with the k6 scenarios in [`load-tests/`](../load-tests), executed **inside** the cluster
(`load-tests/run.sh`), so they include the gateway, the API, PostgreSQL, Redis and token validation.

**Environment:** MicroK8s 1.34 in a single Multipass VM with **4 vCPU / 6 GB RAM** on an Apple Silicon laptop. k6
itself (about 0.5 CPU at peak), the gateway, PostgreSQL and every API replica share those 4 vCPU, so the absolute
numbers are a lower bound for a real cluster. Comparisons between runs, and the correctness checks, are the
point.

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

## MicroK8s runs (capacity investigation)

The runs below were made earlier on MicroK8s (Multipass VM, 4 vCPU / 6 GB), before the API issued its own tokens.

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
* **Finer-grained cache invalidation.** Invalidate only the changed product's item key, and give lists a separate
  generation or a short, explicitly accepted staleness. Read-heavy traffic would then survive write bursts.
* **PgBouncer** in transaction mode in front of PostgreSQL, sized to the database, for many replicas.
* **Read replicas** for list/search queries, the primary only for writes.
* **HorizontalPodAutoscaler** on API CPU once the database has headroom (metrics-server is enabled; see ADR 0011).

## Reproduce

```bash
load-tests/run.sh smoke
load-tests/run.sh stock-consistency
RATE=150 load-tests/run.sh load
PEAK=2000 load-tests/run.sh stress
kubectl -n product-management scale deployment/api --replicas=4
PEAK=2000 load-tests/run.sh stress
load-tests/run.sh rate-limit
```
