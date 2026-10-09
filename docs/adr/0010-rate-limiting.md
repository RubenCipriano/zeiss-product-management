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

* Token buckets allow short bursts (a page load fires several requests) but cap sustained rates.
* Rejections are `429` problem details with `errorCode: rate_limited` and a `Retry-After` header.
* Health probes are never limited (Kubernetes must always be able to probe).
* The API trusts `X-Forwarded-For` from the gateway because a NetworkPolicy allows API ingress only from gateway
  pods; otherwise the header would be spoofable.
* Limits are configuration (`RateLimiting__*`, `GATEWAY_RATE_LIMIT_*`), not code.

## Consequences

* The API limiter is in memory, per replica: the effective cluster-wide per-user limit is roughly
  `limit × replicas`. That is deliberate. The per-replica limit protects replicas, and exact global quotas would need
  a shared store (Redis-backed limiter), adding a network hop to every request. It is the next step if contractual
  quotas are needed.
* Load tests that originate from a single pod IP would hit the gateway's per-IP limit. `load-tests/run.sh` raises it
  for stress and spike runs only and restores it afterwards, and simulates 10 distinct users for the per-user limits.
