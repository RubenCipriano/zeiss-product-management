# 0017 — Idempotency keys for create and stock changes

**Status:** accepted

## Context

`POST /api/products`, `decrement-stock` and `add-to-stock` are not idempotent: sending one twice creates two
products or moves stock twice. Requests get sent twice all the time: a client times out and retries, a user double
clicks, a mobile network drops the response but not the request. With several API replicas behind a load balancer,
the copies can land on **different replicas at the same moment**, so an in-memory "seen it" list does not help.

## Decision

Clients may send an **`Idempotency-Key`** header (the IETF *Idempotency-Key HTTP header field* draft) on the three
endpoints. The API then guarantees: **one key, one execution**, on any replica.

* **Claim atomically in Redis.** Before running the request, the replica runs `SET product-management:idempotency:{client}:{key} <in progress>
  NX PX <timeout>`. Redis executes commands one at a time, so of two simultaneous copies exactly one wins, whichever
  replica it reached.
* **Store the outcome.** When the request finishes, the status, body and relevant headers (`ETag`, `Location`) are
  stored under the key for 24 hours, together with a fingerprint of the request (`SHA-256` of method, path, query and
  body).
* **Answer copies from the store.**

  | The key is…                               | Response |
  | ----------------------------------------- | -------- |
  | new                                       | the request runs normally |
  | completed, same request                   | the stored response, plus `Idempotent-Replayed: true` |
  | still running (claimed by another copy)   | `409 idempotency_key_in_progress`: retry shortly |
  | completed, **different** request          | `422 idempotency_key_reused`: a client bug, never silently ignored |
  | invalid (empty, > 128 chars, odd chars)   | `400` |

* **Rejections are outcomes too.** A `409 insufficient_stock` is stored and replayed: a retry of a refused order
  must not succeed later just because stock arrived in between. Server errors (5xx) and exceptions are *not*
  stored: the key is released and the client can retry.
* **Keys are scoped per consumer** (`{sub}:{key}`): two clients cannot collide or read each other's responses.
* **Fail closed.** If Redis is unavailable, keyed requests get `503 idempotency_unavailable` instead of running
  without protection. The client asked for exactly-once; running it anyway could break that promise. Requests
  without a key are unaffected (the read cache fails *open* because a stale read is harmless; a duplicate write is
  not).
* **Optional for clients.** The key is not required, to keep the API usable from curl and the original
  specification. The web app always sends one (`crypto.randomUUID()` per user action, reused when the user retries
  after an error), and so do the load tests.
* Implemented as an ASP.NET Core resource filter (`[Idempotent]`), so controllers and services do not know about it.

## Consequences

* Retries are safe end to end, which is why the API itself never retries writes ([0003](0003-atomic-stock-updates.md)).
* Proven by integration tests with two API instances sharing Redis and PostgreSQL: 20 simultaneous copies split
  across both replicas move the stock once; a retry on the other replica is replayed; a reused key with a different
  body is refused; without Redis keyed requests are refused. And at scale by the `teams` load test, which sends 30 %
  of its stock changes twice (sequentially or simultaneously) and checks that the ledger gained exactly one row per
  applied change ([0018](0018-autoscaling-and-scale-testing.md)).
* Redis holds up to 24 hours of responses. They are small (one product), and the key expires on its own.
* The guarantee depends on Redis keeping the claim. Redis here runs without persistence: if it restarts while a
  request is in flight, a copy arriving afterwards could run again. A production setup would use a replicated Redis
  (or store keys in PostgreSQL in the same transaction as the change, at the cost of a write per request).
* A request that crashes the pod between the database commit and storing the response leaves an "in progress" claim
  that expires after a minute; a retry after that would run again. The window is milliseconds wide; closing it
  completely needs the key written in the same database transaction.
