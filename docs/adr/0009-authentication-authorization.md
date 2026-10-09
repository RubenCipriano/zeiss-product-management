# 0009 — API-issued JWTs for registered consumers, permission-based RBAC

**Status:** accepted (supersedes an earlier version that used Keycloak as an external identity provider)

## Context

The API must know who calls it and what they may do. A full identity provider (Keycloak, Entra ID) is the right tool
when there are many applications, single sign-on, MFA or user self-service. Here there is one API and a small, known
set of callers: the web app's users, the MCP server, load tests and other services. A separate IdP would add a
component to run, secure and explain, without solving a problem this system has.

## Decision

**The API issues and verifies its own JWT access tokens.**

* **Consumers.** Every caller is a registered *API consumer* with a client id, a secret and roles. Consumers live in
  configuration: a Kubernetes Secret mounted as `config/consumers.json`, or `appsettings.Development.json` locally.
  Only **PBKDF2-SHA256 hashes** of the secrets are stored (salted, 100,000 iterations). The
  `dotnet ProductManagement.Api.dll hash-secret <secret>` command generates them.
* **Token endpoint:** `POST /api/auth/token` with `{ "clientId", "clientSecret" }` returns
  `{ "accessToken", "tokenType": "Bearer", "expiresIn" }`.
  * The token is a JWT signed with **HMAC-SHA256**. Claims: `iss`, `aud`, `sub` (client id), `roles`, `jti`, `iat`,
    `nbf`, `exp`. Default lifetime: 1 hour.
  * Unknown client and wrong secret give the **same** 401 (`invalid_client`) and take the same time: the secret is
    verified against a dummy hash when the client does not exist, and compared in constant time. An attacker cannot
    find out which client ids exist.
* **Verification on every request** (`AddJwtBearer`): signature, `HS256` only (no algorithm confusion), issuer,
  audience, lifetime (30 s clock skew). Claims keep their issued names (`MapInboundClaims = false`).
* **One key for all replicas.** The signing key (≥ 256 bits, Base64) comes from a Kubernetes Secret, so a token
  issued by one replica is accepted by every replica. The API refuses to start with a missing or short key, or with
  a consumer that has no valid hash.
* **Permissions, not roles, on endpoints.** Policies are named after permissions and mapped from roles in one place
  (`Policies.Configure`):

  | Policy           | Roles                        | Endpoints                     |
  | ---------------- | ---------------------------- | ----------------------------- |
  | `products:read`  | viewer, stock-clerk, manager | every GET                     |
  | `stock:write`    | stock-clerk, manager         | decrement-stock, add-to-stock |
  | `products:write` | manager                      | POST, PUT, DELETE             |

* **Secure by default:** a fallback policy requires an authenticated caller. Only the token endpoint, health probes
  and the OpenAPI document are anonymous, explicitly.
* **Brute-force protection** on the token endpoint, in two layers:
  1. a per-replica rate limit (20 requests per minute per IP);
  2. a **cluster-wide count of failed attempts per IP in Redis**. After 10 failures in a minute the IP gets `429`
     with `Retry-After`, even with the right secret.

  The second layer exists because the first does not hold when attempts are spread over replicas. Without Redis it
  falls back to an in-memory counter; if Redis is down it fails open and the first layer still applies.
* **Web app:** a login page posts the credentials to the token endpoint. The session (token, expiry, roles read from
  the token) lives in `sessionStorage`: it survives a reload and ends when the tab closes or the token expires. The
  token is attached only to `/api` calls. A 401 ends the session and returns to the login page with a safe
  `returnUrl`. The UI hides actions the role cannot perform; the API enforces them.
* **MCP server:** a consumer like any other (`mcp-server`, role `stock-clerk`), with its token cached until shortly
  before it expires.

## Consequences

* No external identity component to run. Tests use the same signing key as the test host, so the real validation
  pipeline runs. Covered: the role × operation matrix (30 cases); expired, foreign-audience and forged tokens; the
  token endpoint (valid, invalid, missing, throttled); and failed attempts adding up across two replicas.
* Tokens cannot be revoked before they expire (stateless JWTs). Mitigations: a short lifetime, and rotating the
  signing key invalidates all tokens at once. If per-token revocation is needed, keep a deny-list of `jti` values in
  Redis.
* HMAC means every verifier holds the signing key. That is fine while only the API verifies tokens. If other
  services must verify them without being able to issue them, switch to RS256/ES256 and publish the public key
  (JWKS). This is a configuration change in the issuer and the bearer options.
* Consumers are managed in configuration (a deploy to add one). At larger scale, consumers move to a database table
  with an admin endpoint, or the system adopts a real identity provider, and the API's verification side stays the
  same.
* Behind a load balancer that hides client addresses (Docker Desktop's localhost LB does SNAT), every caller appears
  with the same IP, and the failed-attempt throttle then applies to everyone together. In production, preserve the
  client IP (`externalTrafficPolicy: Local` or the PROXY protocol) so it applies per caller.
