# Product Management — Web and Gateway

This repository holds two things that are built and released together:

* the **web app**: an Angular 22 single-page application (Tailwind CSS, Angular CDK) where people sign in, browse
  products, change stock and manage the catalogue, according to their role;
* the **gateway**: an nginx server in front of the whole application (web app and API), with its own logic for
  routing, token checks, rate limits, caching, security headers and tracing.

It contains their code, the unit and end-to-end tests, the two container images and the CI pipeline that tests, builds,
scans and releases them. The API is in [backend](https://gitlab.com/zeiss/backend); the Kubernetes manifests, the
environments and the scripts that deploy everything are in [deploy](https://gitlab.com/zeiss/deploy).

## Contents

- [The gateway](#the-gateway)
  - [What a gateway is](#what-a-gateway-is)
  - [Why this project has one](#why-this-project-has-one)
  - [What it does, request by request](#what-it-does-request-by-request)
  - [The gateway and the Ingress](#the-gateway-and-the-ingress)
- [The web app](#the-web-app)
  - [What it does for a user](#what-it-does-for-a-user)
  - [How it is organised](#how-it-is-organised)
  - [State and data flow](#state-and-data-flow)
  - [Errors](#errors)
  - [Browser error reporting](#browser-error-reporting)
  - [Accessibility](#accessibility)
  - [Design decisions](#design-decisions)
- [Configuration](#configuration)
- [Run locally](#run-locally)
- [Scripts](#scripts)
- [Tests](#tests)
- [The images](#the-images)
- [CI](#ci)
- [Troubleshooting](#troubleshooting)

## The gateway

### What a gateway is

A **gateway** (or **reverse proxy**) is a server that sits in front of an application and receives every request
first. The browser only ever talks to the gateway, at one address; the gateway forwards each request to the right
internal service and returns its answer. Because everything passes through it, it is also the natural place for
concerns that every request shares: authentication checks, rate limiting, caching, security headers, logging.

### Why this project has one

* **One origin.** The browser loads the app and calls the API at the same address (`/` and `/api`). There is no
  cross-origin request, so no CORS configuration, and one strict Content-Security-Policy covers everything.
* **Many API replicas.** The API runs as 2 to 8 replicas. The gateway spreads requests over them per request and picks
  up new replicas within seconds (see [Other API requests](#other-api-requests)).
* **Protect the API.** Forged or expired tokens and floods from one client are stopped before they reach the API.
* **Absorb read bursts.** Identical reads within one second are answered once.
* **Same behaviour everywhere.** The gateway runs in every environment, locally too, so the application behaves the
  same on a laptop and in production.

### What it does, request by request

The routing is in [`gateway/default.conf.template`](gateway/default.conf.template). In order of precedence:

| Path | Goes to | What happens on the way |
| --- | --- | --- |
| `/healthz` | answered by the gateway | `200 ok`, not logged (Kubernetes' probes) |
| `/api/auth/token` | API | rate limit per client IP; no token needed (this is where tokens come from); never cached |
| `/api/*` (any other) | API | rate limit per client IP; token verified; GET/HEAD answers cached for 1 s |
| `/collect` (POST only) | log collector (Grafana Alloy) | a much smaller rate limit; body at most 64 KB; `204` when there is no collector |
| `*.js`, `*.css`, fonts, `*.ico`, `*.svg`, `*.png` | web | kept in an edge cache for 7 days |
| `/` and everything else | web | the Angular app (`index.html` for app routes) |

For every response, the gateway adds the [security headers](#security-headers-and-csp) and writes one
[JSON access-log line](#tracing-and-the-access-log).

#### The token endpoint

Sign-in. The client sends `{"clientId": "...", "clientSecret": "..."}` and the API answers with an access token. The
gateway only applies the per-IP rate limit; the API has its own stricter limits for this endpoint (20 requests per
client IP per minute, and a cluster-wide block after 10 failed sign-ins per minute).

#### Other API requests

1. **Rate limit per client IP.** `GATEWAY_RATE_LIMIT_RPS` requests per second (default 200) with a burst of
   `GATEWAY_RATE_LIMIT_BURST` (default 400); above that the gateway answers `429 Too Many Requests`. This is coarse
   protection; the API applies finer limits per signed-in user behind it.
2. **Token check at the edge.** Before proxying, nginx makes an internal sub-request (`auth_request /_jwt`) to a small
   JavaScript function that runs inside nginx ([`gateway/jwt.js`](gateway/jwt.js), nginx's built-in **njs** engine).
   It verifies the `Authorization: Bearer <token>` header:
   * the token is a JWT signed with **ES256** (an elliptic-curve signature). Any other algorithm (`none`, `HS256`) is
     refused before any cryptography;
   * the signature is checked with the API's **public** key, downloaded from the API's JWKS document
     (`JWKS_URL`, normally `http://api.product-management.svc.cluster.local:8080/.well-known/jwks.json`). The gateway
     holds no secret: it can verify tokens but never issue one. Keys are cached for 1 hour; a token with an unknown
     key id (after a key rotation, or an attacker's random value) triggers at most one new download per 30 seconds;
   * issuer and audience must equal `JWT_ISSUER` and `JWT_AUDIENCE` (`product-management-api`); `exp` and `nbf` are
     checked with 30 s of clock tolerance; the token must carry at least one role;
   * a verified token is remembered (in shared memory, until it expires and at most 10 minutes), so the signature is
     checked once per token, not once per request.

   A failed check is answered by the gateway with `401` and the API's error format, without reaching the API:

   ```json
   {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.2","title":"Unauthorized","status":401,
    "detail":"A valid access token is required (token expired).","errorCode":"invalid_token"}
   ```

   Why at the edge: forged, expired or malformed tokens cost the API nothing, and the gateway learns the caller's
   *verified* roles, which the cache below needs. The API still validates every token itself (defence in depth).
3. **Microcache** (a cache that keeps answers for a very short time). Identical `GET`/`HEAD` requests within **1
   second** are answered by nginx from one stored `200` response:
   * the cache key is `method | URI with query | roles`, the roles taken from the token just verified (sorted, e.g.
     `manager` or `stock-clerk`). Every read endpoint of the API returns the same data to everyone allowed to call it,
     so users with the same roles can share an entry; a response that differed per user would have to say
     `Cache-Control: private` (nginx never stores those);
   * writes (`POST`, `PUT`, `DELETE`) are never cached;
   * a request with `Cache-Control: no-cache` (or `Pragma`) skips the cache: the web app sends it right after the
     user's own change, so they always see its result;
   * concurrent misses for the same key wait for one upstream request (`proxy_cache_lock`);
   * the response header `X-Cache-Status` says `HIT`, `MISS`, `BYPASS`, ...

   Why: it flattens bursts (many users on the same page, dashboards, the app's 15-second refresh) while staying at
   most one second behind.
4. **Load balancing.** The API's Kubernetes Service is *headless*: its DNS name returns the address of every ready API
   pod. nginx re-resolves it every 5 seconds and sends each request to the pod with the fewest requests in progress
   (`least_conn`), over kept-alive connections. With a normal Service, the kept-alive connections would stay on the
   old pods and new replicas would get no traffic. If a pod fails or times out before answering, an idempotent request
   is retried once on another one (`proxy_next_upstream error timeout http_502 http_503`, 2 tries). Timeouts: 3 s to
   connect, 30 s to read.
5. **Forwarded headers.** The API receives `X-Forwarded-For`, `X-Forwarded-Proto`, `X-Real-IP`, the original `Host`
   and a `traceparent`.

#### Browser error reports

The web app reports its errors here (see [Browser error reporting](#browser-error-reporting)). Only `POST` is allowed,
bodies up to 64 KB, at most 5 requests per second per client IP with a burst of 20 (a page stuck in an error loop is
cut off here, not at the log store). The destination is `COLLECTOR_UPSTREAM`, Grafana Alloy's receiver in the cluster
(`alloy-faro.infra.svc.cluster.local:12347`). When `COLLECTOR_UPSTREAM` is empty, [`gateway/collector.sh`](gateway/collector.sh)
configures `/collect` to answer `204 No Content` and drop the report, so the browser sees no error. If the collector is
configured but not running, `/collect` answers `502` and the browser drops the report.

#### Static files and the app

Files with a content hash in their name (`main-ABC123.js`) never change, so the gateway keeps them in an edge cache
(on its own disk, 7 days, 256 MB) and the web pods serve each file once. Everything else (including `index.html`) is
passed to the web pods on every request.

#### Security headers and CSP

[`gateway/security-headers.conf`](gateway/security-headers.conf) adds to every response:

| Header | Value | Why |
| --- | --- | --- |
| `Content-Security-Policy` | `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'` | see below |
| `X-Content-Type-Options` | `nosniff` | the browser must not guess file types |
| `X-Frame-Options` | `DENY` | no other site may show the app in a frame (clickjacking) |
| `Referrer-Policy` | `strict-origin-when-cross-origin` | other sites see only the origin |
| `Permissions-Policy` | camera, microphone, geolocation, payment, usb off | the app needs none of them |
| `Cross-Origin-Opener-Policy`, `Cross-Origin-Resource-Policy` | `same-origin` | isolation from other origins |

A **Content-Security-Policy (CSP)** tells the browser where a page may load code and data from. Here: only from its own
origin, no inline scripts, no frames. An injected `<script>` (cross-site scripting) is then not executed, which matters
because the session token lives in the browser's `sessionStorage`. Inline *styles* are allowed because Angular adds
component styles as `<style>` elements; styles cannot run code. `Strict-Transport-Security` is not sent by the gateway
(it does not know whether TLS is in front); add it where HTTPS terminates.

#### Tracing and the access log

Every request gets a **trace id** ([`gateway/trace.js`](gateway/trace.js)). A valid incoming W3C `traceparent` header is
kept; otherwise one is made from nginx's random `$request_id`. The gateway sends `traceparent` to the API, which puts the
same trace id on every log line it writes for that request. The gateway writes one JSON line per request to stdout:

```json
{"time":"...","level":"info","component":"gateway","trace_id":"...","request_id":"...","user":"manager",
 "remote":"10.42.0.17","method":"GET","uri":"/api/products?page=1&pageSize=20","status":200,"bytes":5120,
 "duration":0.012,"upstream":"10.42.0.31:8080","upstream_duration":"0.011","cache":"MISS","user_agent":"..."}
```

`user` is the verified token's subject (the client id). In Grafana, one search by `trace_id` shows the gateway's line and
all of the API's lines for that request.

#### The real client address

Behind an Ingress controller every connection comes from the controller's pod, so every client would share one
address: one rate-limit bucket for everyone, and logs that cannot tell clients apart. The gateway therefore takes the
client address from `X-Forwarded-For`, but **only** when the connection comes from `GATEWAY_TRUSTED_PROXIES`. The
default `127.0.0.1/32` trusts nobody, which is right when clients connect directly (locally); staging and production
set the Ingress controller's pod network (k3s: `10.42.0.0/16`). The API in turn trusts the gateway's
`X-Forwarded-For`, because a Kubernetes NetworkPolicy lets only the gateway reach it.

### The gateway and the Ingress

In staging and production both exist, one behind the other:

```
browser ──HTTPS──► Ingress controller (Traefik) ──HTTP──► gateway (nginx) ──► web / API / Alloy
                   cluster's front door                  application's front door
                   TLS, host name                        paths, tokens, limits, cache, headers, trace id
```

The **Ingress** belongs to the cluster: it accepts HTTPS for every application on it, chooses the application by host
name and terminates TLS (certificates from cert-manager). The **gateway** belongs to this application and carries its
logic. Locally there is no Ingress: Kubernetes publishes the gateway directly on http://localhost:8080. Keeping the
logic in the gateway means it does not depend on which Ingress controller a cluster has. The deploy repository's
README explains Ingress, TLS and cert-manager in detail.

## The web app

### What it does for a user

* **Sign in** with a client id and secret (`/login`); the session lasts as long as the token (1 hour) or until the tab
  is closed. Signing out, or an expired token, sends the user back to the login page, and then to the page they were on.
* **Products page** (`/products`): a paged table (20 per page) with each product's name, category, price, stock with
  its status (out of stock at 0, low stock from 1 to 10, otherwise in stock) and last update. Search by part of the name; filter by a stock
  range or with presets (low stock, out of stock). Summary cards: total products, low stock, out of stock.
* **Stock:** `+` / `−` in each row changes the stock in one click by a chosen step (1, 5, 10, 25 or 100); a dialog
  adds or removes an exact amount. Each product's **stock history** (every addition, removal and correction, with who
  and when) opens in a dialog.
* **Catalogue:** create, edit and delete products (delete asks for confirmation).
* **Roles:** a `viewer` only reads; a `stock-clerk` also changes stock; a `manager` does everything. Buttons a user may
  not use are not shown. The API enforces the same rules on every request.
* **Stays current:** the page reloads after every change, refreshes itself every 15 seconds while the tab is visible
  (so changes made by others appear), and when someone else edited a product meanwhile (`412`) it says so and offers the
  latest version instead of overwriting their change.
* **Feels fast:** pages already seen appear at once from a browser cache, the next page is loaded ahead of time, and a
  loading bar replaces an emptied table.

### How it is organised

```
src/
  main.ts, index.html, tailwind.css
  app/
    app.ts, app.html            the shell: top bar, router outlet, toasts
    app.config.ts               app-wide providers: router, HTTP client and its interceptors, error reporting, currency (EUR)
    app.routes.ts               /login, /products (signed in only), everything else → /products; pages are lazy-loaded
    core/                       app-wide services, created once
      auth/                     Auth (sign-in, session, roles → permissions), authGuard, authInterceptor
      config/                   API_BASE_URL (/api)
      http/                     ApiError + apiErrorInterceptor; browser cache: ResponseCache + clientCacheInterceptor
      notifications/            Notifications (toast messages)
      observability/            BrowserErrors (Grafana Faro, loaded lazily), ReportingErrorHandler
    shared/                     reusable building blocks with no product knowledge
      ui/                       AppDialog, ConfirmDialog, icon, paginator, progress bar, toast outlet
      util/                     autoRefresh(), newIdempotencyKey()
      forms/                    validators, validation messages
      pipes/                    signedNumber
      models/                   PagedResponse
    features/
      auth/login/               the sign-in page
      products/
        data-access/            ProductsApi, CategoriesApi (HTTP), ProductsStore (state), the models and business limits
        feature/                ProductsPage (the container), ProductActions (what a user can do), the three dialogs
        ui/                     presentational: InventorySummary, ProductFilters, ProductTable, StockStatus, pipes
  testing/                      test fixtures (fake auth, sample products)
e2e/                            Playwright end-to-end tests and the Lighthouse user flow
gateway/                        the gateway image
ci/release.sh                   the release jobs' script
```

The dependency rule inside a feature, so each piece has one reason to change:

| Folder | Holds | May use | Never |
| --- | --- | --- | --- |
| `ui/` | components that only display and emit events (`input()`, `output()`) | `shared/ui`, pipes | services, the store, HTTP |
| `feature/` | the page (wires store and actions to `ui/`), `ProductActions` (opens dialogs, shows results), the dialogs | `data-access/`, `ui/`, `shared/`, `core/` | HTTP directly |
| `data-access/` | HTTP clients and the store | `core/`, `shared/` | components |

Across folders, imports go through the folder's `index.ts`; inside a folder, files import each other directly. These
rules are conventions checked in review, not by the linter. Components are standalone (no NgModules): each imports
exactly what it uses.

### State and data flow

The app uses **signals**, Angular's reactive values: a template or a `computed` value that reads a signal updates
by itself when the signal changes. There is no zone.js; the screen updates because a value changed.

**`ProductsStore`** ([`products-store.ts`](src/app/features/products/data-access/products-store.ts)) holds the
products page's state and lives as long as the page:

* `query`: what the user asked to see (filter, page, page size). Changing it is the only way to load data.
* `rxResource` loads the page for the current `query` and **cancels the request in flight** when the query changes, so
  a slow older page can never overwrite a newer one. A `linkedSignal` keeps the last loaded page on screen while the
  next one loads (or after it fails), so the table never flashes empty.
* The three summary counts come from the ordinary list endpoints with `pageSize=1` (only `totalCount` is used): all
  products, stock 0 to 10 (low stock, the empty ones included), stock 0 to 0 (out of stock). No statistics endpoint is
  needed, and the counts are cached like any read.
* Every 15 seconds, while the tab is visible and nothing is loading, it reloads (`autoRefresh`); these background reloads
  may be answered by the caches.
* After a page loads it prefetches the next one, so "next page" is usually instant.

**One click, end to end** (a stock clerk presses `−5` on a product):

1. `ProductTable` (ui) emits "remove 5 from product 100042". It knows nothing else.
2. `ProductActions.quickChange` (feature) creates a new **idempotency key** (a random UUID) for this click, marks the
   row as busy and calls the store.
3. `ProductsStore.adjustStock` calls `ProductsApi.adjustStock`, which sends
   `POST /api/products/100042/decrement-stock/5` with the header `Idempotency-Key: <uuid>`.
4. The interceptors run: the browser cache is skipped (not a GET) and will be emptied when the request completes; the
   access token is added; on the way back any error becomes an `ApiError`.
5. The gateway checks the token and rate limit and passes the request to an API replica, which changes the stock in
   one SQL statement. If the same request arrives twice (a double click, a network retry), the key makes the API apply
   it once.
6. On success, the store puts the returned product into the table at once, then reloads the page and the counts
   bypassing every cache (`Cache-Control: no-cache`), because the change may move the product out of the current
   filter. On error, a toast shows the API's message (for example "only 3 available").

**Browser cache** ([`client-cache-interceptor.ts`](src/app/core/http/client-cache-interceptor.ts)): an in-memory cache
in the tab, at most 200 responses, for the list and history requests that opt in. A response younger than 5 s is used
without a request; one younger than 5 minutes is shown at once and then replaced by the API's answer
(stale-while-revalidate). Any write empties it, and so does signing out; it is never stored on disk.

**Concurrency:** edits and deletes send the product's version in `If-Match`. If someone else changed it meanwhile, the
API answers `412`; the edit dialog then offers to load the latest version so the user re-applies their change on top
of it.

### Errors

`apiErrorInterceptor` turns every failed HTTP call into one type, `ApiError` (`status`, `code` = the API's `errorCode`,
`message`, field errors). Forms show field errors next to their field (the API names fields as the form does). Other
errors become toast messages; `0` (no network) and `5xx` get a plain message. A `401` from the API (expired or revoked
token) signs the user out and returns them to the login page with a `returnUrl`.

### Browser error reporting

Errors that happen in users' browsers are sent to the same log system as the server logs, so a developer sees them in
Grafana next to the gateway's and API's lines.

* **What is reported:** errors that reach Angular's `ErrorHandler` (uncaught errors, unhandled promise rejections,
  errors in templates and effects) and `console.error` calls. Nothing else: no performance data, no user actions, no
  sessions.
* **How:** the Grafana Faro SDK, loaded with a dynamic `import()` after the first render (it never delays the page),
  posts batches to `/collect` on the same origin; the gateway forwards them to Grafana Alloy, which stores them in
  Loki as `{namespace="browser"}`. Errors raised before the SDK loads are queued (up to 10). Only production builds
  report; the dev server and unit tests do not.
* **Sent with each error:** the stack trace (file names without query strings), the app's release (`APP_VERSION`, the
  image tag), the route without its query string or fragment, the browser's name, version and OS, and the signed-in
  user's id (the token's subject, e.g. `manager`).
* **Never sent:** the token, form values, the full URL, the full user-agent string, language or screen size; no
  session id is stored.
* Reporting can never break the app: every failure in it is swallowed.

### Accessibility

Dialogs use the Angular CDK: keyboard focus stays inside an open dialog and returns to the button that opened it; each
dialog is announced by its title (`aria-labelledby`); confirmations use `role="alertdialog"`. Toasts and the
paginator's range are announced to screen readers (`aria-live="polite"`); the table marks itself busy while loading
(`aria-busy`); icon-only buttons and filter inputs have labels.

### Design decisions

| Decision | Why |
| --- | --- |
| Standalone components and signals, no NgModules, no zone.js | Angular 22's defaults; updates happen because a value changed, which keeps them predictable and testable |
| Three layers per feature (`data-access`, `feature`, `ui`) | a table never calls the API; presentational components are trivial to test and reuse |
| One idempotency key per user action | a retried or double-clicked change is applied once |
| Tailwind CSS + Angular CDK instead of a component library | small bundle (about 94 kB transferred on first load) and accessible behaviour without fighting a theme |
| Same origin through the gateway | no CORS, one CSP, and the token check and caching in one place for every client |
| Session in `sessionStorage`, not `localStorage` | survives a reload, gone when the tab closes; protected by the strict CSP |
| Summary counts from the list endpoints | no extra API endpoint; cached like any read |
| Errors reported without personal data | useful for debugging, safe to store |

## Configuration

**Build time (web image):**

| Setting | Default | Meaning |
| --- | --- | --- |
| `APP_VERSION` (Docker build argument, passed as `ng build --define`) | `dev` | the release the app reports with its errors; the pipeline and the deploy repository's `scripts/deploy.sh local` pass the image tag |

**Run time (gateway image):** environment variables, rendered into the nginx configuration when the container starts
(the official nginx image's template mechanism), so one image serves every environment.

| Variable | Image default | Meaning |
| --- | --- | --- |
| `API_UPSTREAM` | `api.product-management.svc.cluster.local:8080` | the API's headless Service (`host:port`) |
| `WEB_UPSTREAM` | `web.product-management.svc.cluster.local:8080` | the web app's Service |
| `JWKS_URL` | `http://api.product-management.svc.cluster.local:8080/.well-known/jwks.json` | the API's public keys, to verify tokens |
| `JWT_ISSUER`, `JWT_AUDIENCE` | `product-management-api` | the `iss` and `aud` every token must carry (must match the API's settings) |
| `COLLECTOR_UPSTREAM` | `alloy-faro.infra.svc.cluster.local:12347` | where `/collect` forwards browser errors; `""` = answer 204 and drop them |
| `GATEWAY_RATE_LIMIT_RPS` | `200` | requests per second per client IP on `/api` |
| `GATEWAY_RATE_LIMIT_BURST` | `400` | burst above that rate before `429` |
| `GATEWAY_TRUSTED_PROXIES` | `127.0.0.1/32` | the network (CIDR) allowed to set `X-Forwarded-For` |
| `NGINX_ENTRYPOINT_LOCAL_RESOLVERS` | `1` | take the DNS resolver from the container (Kubernetes DNS) |

Fixed in the configuration: the `/collect` limit (5 per second, burst 20, 64 KB), the 1-second microcache, the 7-day
static cache, request bodies up to 1 MB. The deployed values are in the deploy repository
(`apps/frontend/gateway.yaml`, `environments/<env>/gateway.yaml`).

**Development server:** `API_URL` (default `http://localhost:5080`) is where `npm start` proxies `/api`
([`proxy.conf.mjs`](proxy.conf.mjs)).

## Run locally

Requires Node 24 LTS ([`.nvmrc`](.nvmrc); `package.json` accepts `^22.22.3 || ^24.15.0`) and an API to talk to.

| | Install Node 24 |
| --- | --- |
| macOS | `brew install node@24`, or nvm: `nvm install` (reads `.nvmrc`) |
| Windows | `winget install CoreyButler.NVMforWindows`, then `nvm install 24` and `nvm use 24` (or the 24.x installer from https://nodejs.org) |
| Linux (Ubuntu) | nvm (https://github.com/nvm-sh/nvm): `nvm install`; or NodeSource: `curl -fsSL https://deb.nodesource.com/setup_24.x \| sudo -E bash - && sudo apt install -y nodejs` |

The commands are the same everywhere (on Windows in PowerShell, Git Bash or WSL 2):

```bash
npm ci
npm start                                                  # http://localhost:4200, live reload
```

The dev server sends `/api` to `API_URL`:

| The API | How |
| --- | --- |
| on this machine | `dotnet run --project ProductManagement.Api` in the backend repository (its README), then `npm start` |
| the local cluster's | `kubectl -n product-management port-forward deployment/api 5080:8080`, then `npm start`; or through its gateway: `API_URL=http://localhost:8080 npm start` |
| an environment's | `API_URL=https://staging.products.example.com npm start` (the gateway serves the API under the same `/api`) |

Sign in with an account of that API: locally `viewer`/`viewer`, `clerk`/`clerk` or `manager`/`manager`. The dev server
has no gateway: no edge token check, microcache or CSP; the API checks tokens itself.

**The whole system** (gateway, web app, API, PostgreSQL, Redis, logs) on Docker Desktop's Kubernetes (macOS,
Windows) or k3s on an Ubuntu server: clone the three repositories side by side and follow the deploy repository's
README (`scripts/infra.sh up`, then `scripts/deploy.sh local`, which builds this repository's two images) →
http://localhost:8080 (on an Ubuntu server also http://&lt;server-ip&gt;:8080).

## Scripts

| Script | What it does |
| --- | --- |
| `npm start` | development server with live reload on http://localhost:4200; `/api` → `API_URL` |
| `npm run build` | production build → `dist/product-management-web/browser/` |
| `npm run watch` | development build, rebuilt on every change |
| `npm test` | unit and component tests (Vitest, through `ng test`), watch mode |
| `npm run test:ci` | the same, single run |
| `npm run lint` | ESLint with angular-eslint |
| `npm run format` / `npm run format:check` | Prettier: write / check (`src/**/*.{ts,html,scss}`, `e2e/**/*.ts`) |
| `npm run e2e` | Playwright end-to-end tests against a running system ([Tests](#tests)) |
| `npm run audit:lighthouse` | Lighthouse user flow, signed in (mobile; `FORM_FACTOR=desktop` for desktop); report in `e2e/reports/` |
| `ci/release.sh` | the release jobs' script ([CI](#ci)) |

## Tests

**Unit and component tests:** 187 tests in 35 spec files, next to the code they test (`*.spec.ts`), run with Vitest in
a simulated browser (jsdom). They cover the store (paging, cancelling, keeping the last page, refreshes, prefetch,
applying changes), the API clients (URLs, headers, idempotency keys, `If-Match`), the interceptors (token, errors,
browser cache), auth and the guard, error reporting (what is and is not sent), the dialogs (validation, 412 handling),
the table, filters, paginator and toasts. `npm run test:ci`; one file: `npx ng test --watch=false --include src/app/core/auth/auth.spec.ts`.

**End-to-end tests** ([`e2e/`](e2e)): Playwright drives a real Chrome through the real gateway, API, Redis and
PostgreSQL, on a desktop (1280×800) and a phone (390×844) viewport: 11 scenarios × 2 = **22 tests**. They need a
running system, so they are not a CI job.

| Spec | Scenarios |
| --- | --- |
| `auth.spec.ts` | a signed-out visitor is sent to the login page and back; wrong credentials are refused without saying which part was wrong; signing out ends the session; a viewer reads and sees history but cannot change anything; a stock clerk changes stock but cannot create products |
| `products.spec.ts` | as manager: create, find, change stock by 10, see the history, edit and delete |
| `paging.spec.ts` | paging shows new rows, and pages already seen appear at once from the browser cache (with the network slowed down) |
| `edge.spec.ts` | the gateway sends the CSP and security headers; refuses API calls without a valid token before they reach the API; a missing file is a 404 and `robots.txt` keeps robots out |
| `browser-errors.spec.ts` | an uncaught error in the page is stored in Loki with the route and the user but no query string (skipped when Loki is not reachable) |

Every test also fails on a CSP violation or an uncaught page error. `global-setup.ts` signs each role in once through
the API and reuses the session (the token endpoint is rate limited per IP). The tests clean up what they create.

```bash
npx playwright install chromium                    # once, if Google Chrome is not installed (then PLAYWRIGHT_CHANNEL=chromium)
npm run e2e                                        # against http://localhost:8080 with the local demo accounts
BASE_URL=http://<server-ip>:8080 npm run e2e       # against the deploy repository's k3s on an Ubuntu server
kubectl -n infra port-forward svc/loki 3100:3100 & # optional: the browser-errors test reads Loki (LOKI_URL, default http://localhost:3100)
BASE_URL=https://staging.products.example.com \
E2E_VIEWER_SECRET=… E2E_CLERK_SECRET=… E2E_MANAGER_SECRET=… npm run e2e     # against an environment
```

Against an environment, the accounts `viewer`, `clerk` and `manager` must exist in its API, with their secrets in
`E2E_<ROLE>_SECRET`. The paging test needs more than three pages of products (over 60); a fresh database has the 24
sample products, so add some first. Reports: `e2e/reports/playwright/`; traces of failed tests: `test-results/`.

## The images

| Image | Built from | Contents |
| --- | --- | --- |
| `web` | [`Dockerfile`](Dockerfile) + [`nginx.conf`](nginx.conf) | stage 1: `node:24-alpine`, `npm ci`, `npm run build` with `APP_VERSION`; stage 2: `nginxinc/nginx-unprivileged:1.29-alpine` (non-root, port 8080) serving `dist/`. Hashed bundles are cached for a year (`immutable`), `index.html` is always revalidated (`no-cache`), a missing file with an extension is a 404, any other path serves `index.html` (client-side routes), `/healthz` answers `ok`, one JSON log line per request |
| `gateway` | [`gateway/`](gateway) | `nginxinc/nginx-unprivileged:1.29-alpine` with `nginx.conf` (process settings, njs module), `default.conf.template` (routing), `security-headers.conf`, `jwt.js`, `trace.js`, and `collector.sh` (run at startup: forwards `/collect` or answers 204) |

```bash
docker build --build-arg APP_VERSION=dev -t product-management/web .
docker build -t product-management/gateway gateway
```

## CI

[`.gitlab-ci.yml`](.gitlab-ci.yml). The pipeline tests, builds, scans and releases the two images. It never applies
anything to a cluster: an environment runs the tags in the deploy repository's
`environments/<env>/kustomization.yaml`, and the deploy repository's pipeline applies them.

| Job | Stage | When | Does |
| --- | --- | --- | --- |
| `test:frontend` | test | every pipeline | `npm ci`, lint, format check, unit tests (JUnit report), production build |
| GitLab SAST, Secret Detection | test | every pipeline | GitLab's own analysers (included templates) |
| `build:web`, `build:gateway` | build | every pipeline | build and push `registry.gitlab.com/zeiss/frontend/{web,gateway}:<short sha>` (BuildKit on Docker-in-Docker, layer cache in the registry); on a tag pipeline also `:<tag>` |
| `scan:web`, `scan:gateway` | scan | every pipeline | Trivy: full report to GitLab; fails on fixable critical vulnerabilities |
| `release:staging` | staging | `main`, `STAGING_ENABLED` | `ci/release.sh staging`: commits `frontend: web, gateway <sha>` (both `newTag`s) to the deploy repository's `main` |
| `release:production` | production | `main`, `STAGING_ENABLED` and `PRODUCTION_ENABLED`, **manual** | `ci/release.sh production`: pushes a branch to the deploy repository and opens the merge request "Release web, gateway `<sha>` to production"; merging it is the release |

**`ci/release.sh`** (the same script is in backend): `ci/release.sh <staging|production> <image>=<tag> ...`. It clones
the deploy repository with `DEPLOY_REPO_TOKEN` (through a git credential helper, so the token is in no URL or log),
sets each image's `newTag` with `yq`, and either commits to `main` and pushes (staging; if `main` moved meanwhile,
for example because the backend released at the same moment, it starts again from the new `main`, up to 5 times) or
pushes the branch `release/production/<project>-<tag>` with push options that open a merge request (production). It
does nothing if the tags are already there. `DRY_RUN=1` prints the commit and push without pushing.

| Variable | Scope | What |
| --- | --- | --- |
| `STAGING_ENABLED` | project or group | `"true"` creates `release:staging` on `main` |
| `PRODUCTION_ENABLED` | project or group | `"true"` creates `release:production` (manual); needs `STAGING_ENABLED` too |
| `DEPLOY_REPO_TOKEN` | project; masked, protected | a project access token of the deploy repository, role Maintainer, scope `write_repository` |

Nothing else is needed: the cluster pulls the images with the zeiss group's deploy token, which the deploy
repository's pipeline stores in the cluster. Until `STAGING_ENABLED` is set the pipeline stops, green, after the scans.
A rollback is a revert of the release commit (or merge) in the deploy repository.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `npm start`: every API call fails and the terminal shows proxy errors (`ECONNREFUSED`) | nothing listens on `API_URL` | start the API (`dotnet run`), port-forward it, or set `API_URL` |
| sign-in fails with `invalid_client` | wrong account for that API | locally `viewer`/`viewer`, `clerk`/`clerk`, `manager`/`manager` |
| every API call answers `401 invalid_token` at the gateway | expired token, or the API's signing key changed | sign in again; check `JWKS_URL` reaches the API (the gateway logs `JWKS download failed`) |
| `429 Too Many Requests` during a load test | the per-IP limit (200/s, burst 400) or the API's per-user limits | raise `GATEWAY_RATE_LIMIT_*` in the deploy repository, or spread the test over users |
| `/collect` answers 502 | `COLLECTOR_UPSTREAM` is set but Alloy is not running | start the logs (`scripts/infra.sh up logs` in deploy), or deploy with `LOGS=off` (then 204) |
| browser errors never show up in Grafana | development build (reporting is off in dev mode), or no collector | test against a deployed environment |
| a change made elsewhere appears only after up to 15 s | the background refresh interval (plus the 1-second microcache) | expected; your own changes appear at once |
| Playwright: `Cannot sign in as viewer` | the accounts do not exist there, or wrong `E2E_<ROLE>_SECRET` | register the accounts in that API, set the secrets |
| Playwright: paging test fails | fewer than 61 products | create more products first |
| Playwright: Chrome not found | no Google Chrome installed | `npx playwright install chromium` and `PLAYWRIGHT_CHANNEL=chromium` |
| Playwright: browser-errors test skipped | Loki not reachable at `LOKI_URL` | `kubectl -n infra port-forward svc/loki 3100:3100` |
| `npm run audit:lighthouse` cannot launch Chrome | it uses the macOS Chrome path by default | set `CHROME_PATH` |
