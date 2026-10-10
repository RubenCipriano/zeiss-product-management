# 0015 — Load testing with k6, inside the cluster

**Status:** accepted

## Context

"Can the application handle the traffic?" needs evidence: throughput and latency at an expected peak, the breaking
point, behaviour under sudden spikes, and, as important, *correctness* under concurrency when scaled out.

## Decision

[k6](https://k6.io) scripts in `backend/load-tests/`, run **as a Kubernetes Job inside the cluster** (`backend/load-tests/run.sh`), so
results measure gateway + API + PostgreSQL + Redis (and token issuance) rather than a laptop's port-forward tunnel.

| Scenario            | Shape                                               | Pass criteria (thresholds)                      |
| ------------------- | --------------------------------------------------- | ----------------------------------------------- |
| `smoke`             | 1 user, every endpoint, 30 s                        | 100 % checks, 0 errors, p95 < 500 ms            |
| `load`              | realistic mix ramping to 150 req/s, 2 min plateau   | errors < 1 %, p95 < 300 ms, p99 < 800 ms        |
| `stress`            | 50 → 800 req/s in steps                             | reported (find the knee), not enforced          |
| `spike`             | 20 → 500 req/s in 10 s, then back                   | errors < 2 %                                    |
| `stock-consistency` | 300 concurrent orders for 100 units, all replicas   | exactly 100 succeed, 200 get 409, final stock 0 |
| `rate-limit`        | one user at 300 req/s                               | 429s with `Retry-After`, nothing else fails     |

* The virtual users sign in as the three demo accounts (tokens from `POST /api/auth/token`): reads as viewer, clerk
  or manager, stock changes as clerk or manager. Many virtual users share one account, so `run.sh` raises the API's
  per-user limits for every scenario except `rate-limit` and restores them afterwards: the tests measure capacity with
  a few accounts, not many distinct users. (Earlier runs used ten dedicated load-test consumers, since removed.)
  Like a well-behaved client, the consistency test waits for `Retry-After` and retries when it is
  rate limited. Setup creates dedicated products and teardown deletes them.
* `smoke` and `stock-consistency` are the quick checks after a deployment (`backend/load-tests/run.sh smoke`).

## Consequences

* Absolute numbers depend on the machine (a 4-CPU VM locally). The value is in comparing runs, e.g. 2 vs 4 API
  replicas, and in the correctness checks, which must hold anywhere.
* Results of the runs made while building this are recorded in `docs/load-test-results.md`.
