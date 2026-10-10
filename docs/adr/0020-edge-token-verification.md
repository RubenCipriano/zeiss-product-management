# 0020 — Token verification at the gateway and a microcache shared per role

**Status:** accepted

## Context

The gateway's microcache ([0012](0012-gateway.md)) was keyed by the `Authorization` header: a cached response was
only ever reused for the same token. That was the safe choice while the gateway could not tell a genuine token from a
forged one, but it meant one copy of every entry per user. In the read capacity test (ten consumers) only 48–65 % of
requests were answered by nginx, the API did the rest, and one 8-CPU machine topped out at ~5,500 requests/s.

Every read endpoint returns the same data to everyone allowed to call it; what differs is *whether* a caller may.
If the gateway could verify tokens itself, it could share entries between callers with the same roles.

## Decision

* **The API signs with ES256** and publishes its public key at `/.well-known/jwks.json`
  ([0009](0009-authentication-authorization.md)). With HMAC the gateway would need the signing secret, and could then
  mint tokens; with ES256 it holds nothing secret.
* **The gateway verifies every `/api` token** (`frontend/gateway/jwt.js`, nginx njs, through `auth_request`):
  * `alg` must be `ES256` and the `kid` known; the signature is checked with WebCrypto; then `iss`, `aud`, `exp` and
    `nbf` (30 s skew, as the API);
  * keys come from the API's JWKS, downloaded on demand and kept 1 hour; an unknown `kid` (rotation) triggers a new
    download, at most every 30 s once keys are loaded, so random `kid`s cannot flood the API;
  * verified tokens are remembered in shared memory until they expire (at most 10 minutes), so the signature check
    runs once per token per gateway pod, not per request;
  * failures get `401` with the same problem-details shape as the API (`errorCode: invalid_token`) and
    `WWW-Authenticate: Bearer error="invalid_token"`, without reaching the API.
* **The microcache key is `method | URI | roles`**, the roles taken from the verified token. Users with the same
  roles share entries. A response that depends on *who* asks must send `Cache-Control: private` (nginx never stores
  those) or the key must include the subject; today no endpoint does.
* **The API still validates every token** (defence in depth): the gateway is an optimisation and a filter, not the
  authority. The token endpoint stays anonymous at the edge.

## Consequences

* Read capacity on the same machine: **9,712 requests/s** sustained at a 10,000 target (p95 7 ms) and
  **17,359 requests/s** at a 20,000 target (p95 79 ms), 0 errors, 92–94 % answered by nginx. Before: ~5,500.
  Beyond that the load generator itself runs out of CPU ([load-test-results.md](../load-test-results.md)).
* Forged, expired or malformed tokens cost the API nothing.
* The frontend and backend deployments now share three settings (JWKS URL, issuer, audience), set as environment
  variables on the gateway, next to the existing `api-headless` contract (the headless Service is named `api` since
  [0021's update](0021-deployment-layout.md#update-2026-10-10)).
* Revocation is unchanged: tokens stay valid until they expire. A deny-list would have to be checked at the gateway
  too.
* A cached response can be up to 1 s old for everyone with the role, not only for one user; the web app still
  bypasses it right after the user's own changes (`Cache-Control: no-cache`).
