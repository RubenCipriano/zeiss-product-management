# 0012 — nginx gateway in front of the application

**Status:** accepted

## Context

Browsers need one origin for the app, the API and the identity provider (no CORS, cookies and redirects just work).
The API runs as several replicas that come and go, and static assets should be served as cheaply as possible.

## Decision

A dedicated **nginx gateway** (`frontend/gateway`) is the only entry point:

| Path      | Upstream                  | Notes                                                             |
| --------- | ------------------------- | ----------------------------------------------------------------- |
| `/api/*`  | API replicas              | per-IP rate limit, `least_conn`, keep-alive pool, retry on another replica for connect errors/502/503 |
| `/auth/*` | Keycloak                  | larger proxy buffers for the token responses                      |
| static    | web (Angular)             | **edge cache** for hashed bundles (`X-Cache-Status`), gzip         |
| `/*`      | web                       | SPA routes                                                        |

* **Per-request load balancing across API replicas:** the upstream points at the API's *headless* Service and uses
  `server … resolve` (open-source nginx ≥ 1.27.3) with a 5 s DNS TTL. nginx sees every ready pod individually and
  picks the least busy one for each request, and new replicas join within seconds of `kubectl scale`. A normal
  ClusterIP Service would balance per TCP connection, and long-lived keep-alive connections would pin traffic to the
  old pods.
* Security headers (`nosniff`, `DENY` framing, referrer policy), `server_tokens off`, 1 MB body limit, JSON access
  logs that include the upstream pod and its latency.
* Configuration is a template rendered from environment variables at startup (rate limits, upstream names), so
  each environment uses the same image.
* The web container is a plain static server (long cache for hashed files, `no-cache` for `index.html`); routing,
  limits and caching live in the gateway.

## Consequences

* Two nginx hops for static files, but after the first request the gateway serves bundles from its cache.
* In a cloud cluster the gateway sits behind the provider's load balancer (Service `LoadBalancer`) or is replaced by
  a Gateway API implementation. The routing rules stay the same.
* TLS terminates in front of the gateway (cloud load balancer / cert-manager). It is not configured for the local
  cluster.
* No Content-Security-Policy yet: Angular's critical-CSS inlining uses an inline `onload` handler, which a strict CSP
  would block. Disabling `inlineCritical` and adding a CSP is listed as a next step.
