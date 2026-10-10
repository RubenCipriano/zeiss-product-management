# 0010 — Rate limiting in two layers

**Status:** accepted

## Context

One misbehaving client (a buggy script, an over-eager AI agent, an attacker) must not degrade the service for
everyone. Limits must also hold with several replicas behind a load balancer.

## Decision

| Layer                  | Key                    | Limit (default)                    | Purpose                                      |
| ---------------------- | ---------------------- | ---------------------------------- | -------------------------------------------- |
| **Gateway** (nginx `limit_req`) | client IP     | 200 req/s, burst 400, `/api` only  | Cheap, early rejection of floods before they reach the API |
| **API** (ASP.NET Core rate limiter) | user (`sub`), IP if anonymous | 50 req/s, burst 100 | Fairness between users; protects each replica |
| **API, writes**        | user                   | 10 req/s, burst 20                 | Stricter for state-changing endpoints        |
| **API, token endpoint** | client IP             | 20 req/min per replica             | Slows secret guessing                         |
| **Failed sign-ins** (Redis) | client IP         | 10 failures/min, **cluster-wide**  | Blocks secret guessing however requests are spread over replicas ([0009](0009-authentication-authorization.md)) |

* Token buckets allow short bursts (a page load fires several requests) but cap sustained rates.
* Rejections are `429` problem details with `errorCode: rate_limited` and a `Retry-After` header.
* Health probes are never limited (Kubernetes must always be able to probe).
* The API trusts `X-Forwarded-For` from the gateway because a NetworkPolicy allows API ingress only from gateway
  pods; otherwise the header would be spoofable.
* Limits are configuration (`RateLimiting__*`, `GATEWAY_RATE_LIMIT_*`), not code. The API validates its limits at
  startup (each at least 1) and refuses to start otherwise.

## Consequences

* The API limiter is in memory, per replica: the effective cluster-wide per-user limit is roughly
  `limit × replicas`. That is deliberate. The per-replica limit protects replicas, and exact global quotas would need
  a shared store (Redis-backed limiter), adding a network hop to every request. It is the next step if contractual
  quotas are needed.
* Load tests that originate from a single pod IP would hit the gateway's per-IP limit. `backend/load-tests/run.sh` raises it
  for stress and spike runs only and restores it afterwards, and simulates 10 distinct users for the per-user limits.
  (Later: the load tests sign in as the three demo accounts only, so `run.sh` raises the per-user limits for every
  scenario except `rate-limit`; [ADR 0015](0015-load-testing.md).)

## Update: limits shared by all replicas

With the autoscaler, per-replica in-memory limits became `limit × replicas`: with 6 API pods a user could send
300 requests/s against a limit of 50, and the `rate-limit` load test stopped seeing any 429. The per-user token
buckets now live in **Redis** (`RedisDistributedRateLimiter`: one Lua script reads, refills and takes atomically,
with Redis's clock), plugged into ASP.NET Core's rate limiter as a custom `RateLimiter`, so policies, `429` and
`Retry-After` are unchanged. If Redis is unavailable each replica falls back to its own in-memory bucket (looser,
never failing requests). Tested with two API instances sharing Redis (`AUsersLimit_IsSharedByAllReplicas`); in the
cluster, with 6 replicas, 300 requests/s from one user → ~72/s accepted (50/s + the burst), 4,566 × 429.

Cost: one Redis round trip per request that reaches the API. Reads answered by the gateway's microcache never reach
it, and do not count against the user: they cost almost nothing.
