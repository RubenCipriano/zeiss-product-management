# Product Management

A product and stock management system:

* **Backend:** .NET 10 / ASP.NET Core Web API, Entity Framework Core and PostgreSQL, with a Redis read cache,
  Keycloak (OIDC) authentication, role-based authorization and rate limiting.
* **Frontend:** Angular 22 with Tailwind CSS, served behind an nginx gateway.
* **Deployment:** Kubernetes (Docker Desktop or MicroK8s), with k6 load tests run inside the cluster.
* **MCP server:** lets AI assistants search the catalogue and record stock movements, under the same rules as
  everyone else.

![Product list](docs/images/product-list.png)

> **".NET Core":** since .NET 5 Microsoft dropped "Core" from the runtime's name. **.NET 10 is the current release
> of .NET Core** (cross-platform, not the Windows-only .NET Framework) and the current LTS. The web and data layers
> keep the name: **ASP.NET Core** and **Entity Framework Core**.

## Contents

- [Run it](#run-it)
- [Develop](#develop)
- [Tests](#tests)
- [Requirements checklist](#requirements-checklist)
- [Architecture](#architecture)
- [API reference](#api-reference)
- [Security](#security)
- [Performance and scaling](#performance-and-scaling)
- [MCP (AI assistants)](#mcp-ai-assistants)
- [Key decisions](#key-decisions)
- [Assumptions](#assumptions)
- [Trade-offs and deviations from common practice](#trade-offs-and-deviations-from-common-practice)
- [Production readiness and next steps](#production-readiness-and-next-steps)
- [Repository layout](#repository-layout)

---

## Run it

Everything runs on Kubernetes. Pick one of two local clusters; the manifests are identical.

### Option A: Docker Desktop's Kubernetes (everything visible in Docker Desktop)

1. Docker Desktop → **Settings → Kubernetes → Enable Kubernetes** → Apply & restart.
2. Deploy (the backend first, then the frontend; each can also be deployed on its own):

   ```bash
   kubectl config use-context docker-desktop
   scripts/deploy-all.sh
   ```

3. Open **http://localhost:8080** (the gateway is a `LoadBalancer` Service on localhost).

### Option B: MicroK8s

```bash
scripts/cluster-up.sh                         # MicroK8s in a Multipass VM (macOS/Windows); native on Linux
export KUBECONFIG="$PWD/.kube/microk8s"
scripts/deploy-all.sh
kubectl -n product-management port-forward service/gateway 8080:8080     # http://localhost:8080
```

### Sign in

| User      | Password  | Role          | Can                                    |
| --------- | --------- | ------------- | -------------------------------------- |
| `viewer`  | `viewer`  | `viewer`      | browse and search                      |
| `clerk`   | `clerk`   | `stock-clerk` | + add and remove stock                 |
| `manager` | `manager` | `manager`     | + create, edit and delete products     |

Demo users exist only in the local realm (`backend/deploy/kubernetes.yaml`).

| What                             | How |
| -------------------------------- | --- |
| Application                      | http://localhost:8080 |
| Swagger UI                       | `kubectl -n product-management port-forward service/api 5080:8080` → http://localhost:5080/swagger |
| Keycloak admin (`admin`/`admin`) | http://localhost:8080/auth/admin |

### What gets deployed

```
                                   ┌───────────────────── namespace product-management ──────────────────────┐
 browser ──► gateway (nginx ×2) ──┬─► web (Angular static files ×2)                                         │
   :8080     rate limit, cache,   ├─► api (ASP.NET Core ×2 … ×N) ──┬─► PostgreSQL (StatefulSet + volume)    │
             per-request LB       │                                └─► Redis (cache)                        │
                                  └─► Keycloak (/auth, OIDC) ◄── the API fetches its signing keys           │
                                      db-migrate Job (EF Core migrations, once per deployment)              │
                                   └──────────────────────────────────────────────────────────────────────────┘
```

Scale the API without touching the frontend:

```bash
kubectl -n product-management scale deployment/api --replicas=4
```

---

## Develop

| Tool      | Version |
| --------- | ------- |
| .NET SDK  | 10.0.401+ (pinned in `backend/global.json`) |
| Node.js   | 24 LTS (`frontend/.nvmrc`; 22.22+ also supported) |
| Docker    | for the integration tests (Testcontainers) and the images |
| kubectl   | for deployment |

**Backend:** open `backend/ProductManagement.sln` in **Visual Studio 2026** (the release that supports .NET 10) or
JetBrains Rider. Projects sit directly under `backend/`, grouped by the `Source`, `Tests` and `Mcp` solution folders.
`ProductManagement.Api.http` has a request for every endpoint. To run the API from the IDE against the cluster's
database:

```bash
kubectl -n product-management port-forward statefulset/postgres 5432:5432    # matches appsettings.Development.json
dotnet run --project backend/ProductManagement.Api
```

**Frontend:**

```bash
cd frontend && npm ci && npm start    # http://localhost:4200; /api proxied to :5080, Keycloak on :8080
```

| Script                 | Purpose |
| ---------------------- | ------- |
| `npm start`            | dev server with live reload |
| `npm test` / `test:ci` | Vitest (watch / single run) |
| `npm run lint`         | ESLint (angular-eslint) |
| `npm run format:check` | Prettier |
| `npm run build`        | production build |

---

## Tests

```bash
cd backend && dotnet test --solution ProductManagement.sln     # 205 tests (Docker must be running)
cd frontend && npm run test:ci                                   # 63 tests
load-tests/run.sh smoke                                          # against the running cluster
```

| Suite                                 | Tests | What it proves |
| ------------------------------------- | ----: | -------------- |
| `Domain.UnitTests`                    | 32 | Invariants (six-digit ids, price precision, stock range); a failed update leaves no partial changes; the stock rule at its edges, including deltas that would overflow naive arithmetic. |
| `Application.UnitTests`               | 55 | Use-case orchestration: validation first, no id consumed by invalid requests, version checks, conflicts mapped to 409/412, stock errors carry the available quantity; validators at their boundaries. |
| `Api.IntegrationTests`                | 96 | Real HTTP against **real PostgreSQL and Redis** (Testcontainers). Every endpoint and the problem-details contract. **Concurrency:** 60 parallel orders on 25 units → exactly 25 succeed; no lost updates; two API instances never produce a duplicate id. **Cache:** hits, invalidation visible across replicas, 404s not cached, works with Redis down. **Security:** 30-case role × operation matrix; missing, expired, foreign and forged tokens; anonymous probes. **Rate limiting:** per user, stricter for writes, `Retry-After`. |
| `Api.AcceptanceTests` (BDD, Reqnroll) | 10 | Gherkin scenarios from the user's point of view (`Features/*.feature`). |
| `Mcp.UnitTests`                       | 12 | MCP tools → API contract, token caching (fake clock, concurrent callers), API errors surfaced to the assistant. |
| Frontend (Vitest)                     | 63 | API client contract (URLs, `If-Match`), error normalisation, store (no duplicate requests, no flicker, background refresh paused in hidden tabs), auth (role → permission mapping, guard, interceptor never leaks the token to other origins), dialogs (validation, server field errors, 412 conflict + reload), role-specific UI, paginator, toasts. |
| Load (k6, in cluster)                 | 6 scenarios | smoke, load (SLO thresholds), stress, spike, **stock consistency across replicas**, rate limiting. See the [results](docs/load-test-results.md). |

CI (`.github/workflows/ci.yml`) runs:
* build with warnings as errors, every test suite, lint and format checks;
* Kubernetes schema validation (kubeconform);
* an **end-to-end job that deploys to MicroK8s on the runner** and runs the k6 smoke and stock-consistency
  scenarios inside the cluster.

---

## Requirements checklist

| Requirement (assessment PDF)                                | Where / how |
| ----------------------------------------------------------- | ----------- |
| CRUD endpoints                                              | `ProductsController`: `GET/POST /api/products`, `GET/PUT/DELETE /api/products/{id}` |
| `POST /api/products/{id}/decrement-stock/{quantity}` → OK    | One atomic conditional UPDATE; 200 + updated product ([ADR 0003](docs/adr/0003-atomic-stock-updates.md)) |
| `POST /api/products/{id}/add-to-stock/{quantity}` → OK       | Same mechanism, upper bound enforced |
| `GET /api/products/search?name=` (partial match)            | Case-insensitive `ILIKE`, wildcards escaped, trigram index |
| `GET /api/products/stock-level?min=&max=`                   | Inclusive range, either bound optional |
| Unique 6-digit id, no duplicates across instances           | PostgreSQL sequence `100000–999999 NO CYCLE` ([ADR 0002](docs/adr/0002-product-id-generation.md)); tested with two API hosts, and with four replicas under load |
| Validation of create/update                                 | FluentValidation + domain guards + database check constraints ([ADR 0005](docs/adr/0005-error-handling.md)) |
| Stock included in every list response                       | `stockQuantity` on every `ProductResponse` |
| EF Core code-first migrations                               | `backend/ProductManagement.Infrastructure/Persistence/Migrations` (generated by `dotnet ef`), applied by a Kubernetes Job |
| Database seeding                                            | Categories via `HasData`; sample products via `UseSeeding`/`UseAsyncSeeding` |
| Angular frontend; reflects changes made through the API     | `frontend/`: every endpoint used; refetch after changes + background refresh ([ADR 0007](docs/adr/0007-frontend-state.md)) |
| Unit tests for various scenarios                            | 99 backend unit tests + 63 frontend tests (plus 106 integration and acceptance tests) |
| *Optional:* BDD tests                                       | `ProductManagement.Api.AcceptanceTests` (Reqnroll / Gherkin) |
| Documentation for running locally                           | This README and [`docs/`](docs) |

---

## Architecture

### Backend (clean architecture)

```
ProductManagement.Api ──► ProductManagement.Application ──► ProductManagement.Domain
        │                           ▲
        └──► ProductManagement.Infrastructure ──► EF Core / PostgreSQL, Redis
ProductManagement.Mcp ──(HTTP)──► the API, through the gateway
```

| Project          | Responsibility |
| ---------------- | -------------- |
| `Domain`         | `Product`, `Category`, invariants, `ProductRules` (single source of limits), the stock rule as an expression. No dependencies. |
| `Application`    | Use cases (`ProductQueryService`, `ProductCommandService`), contracts, FluentValidation, `Result`/`Error`, ports (`IProductRepository`, `IProductQueries`, `IProductIdGenerator`, `IUnitOfWork`). |
| `Infrastructure` | `DbContext`, configurations, migrations, repositories and queries, sequence-based id generator, **Redis cache decorator**, seeding, health checks. |
| `Api`            | Thin controllers, HTTP mapping (status codes, ETag/If-Match, problem details), **authentication, authorization policies, rate limiting**, OpenAPI, health endpoints, the `migrate` command. |
| `Mcp`            | MCP server for AI assistants; a client of the HTTP API. |

A request end to end (`PUT /api/products/{id}`):

1. **Gateway:** checks the per-IP limit and picks the least busy API replica.
2. **API pipeline:** validates the JWT, applies the per-user limit (stricter for writes) and checks the
   `products:write` permission.
3. **Controller:** parses `If-Match` and calls the use case. It has no other logic.
4. **Use case:** validates the request, loads the aggregate, compares versions (412 if stale), checks the category
   and calls `product.Update(...)`. The **domain** enforces the invariants.
5. **Unit of work:** saves. EF Core puts the loaded `xmin` in the `WHERE`, so a concurrent change becomes 412 or
   409. After the commit, the **cache generation** is incremented and every replica stops serving the old data.
6. **Response:** the query side re-reads the product, and the controller returns it with a fresh `ETag`.

**EF Core:**
* reads are no-tracking projections;
* writes load tracked aggregates only for load-modify-save;
* stock changes are atomic `ExecuteUpdateAsync` statements;
* everything is async and honours cancellation, with `DbContext` pooling;
* the database repeats the domain constraints;
* migrations run once per deployment in a Job (EF Core's migration lock makes concurrent runs safe anyway).

### Frontend

```
src/app/
  core/       auth (OIDC, guard, interceptor), http (ApiError + interceptor), config, notifications
  shared/     forms (validators, messages), models, ui (icon, paginator, toasts, confirm dialog)
  features/products/
    data-access/  ProductsApi, CategoriesApi, ProductsStore (signals + rxResource), models
    product-list/, product-form-dialog/, stock-adjust-dialog/, stock-status/
```

The frontend uses Angular 22 (standalone, zoneless, OnPush by default, signals), Tailwind CSS v4 for the look and
Angular CDK for accessible dialogs and menus ([ADR 0013](docs/adr/0013-ui-tailwind-cdk.md)).

---

## API reference

Base path `/api`. Every endpoint requires a bearer token (see [Security](#security)). The full schema is in Swagger.

| Method | Path                                              | Permission       | Success                        | Errors             |
| ------ | ------------------------------------------------- | ---------------- | ------------------------------ | ------------------ |
| GET    | `/products?page=&pageSize=`                       | `products:read`  | 200 `PagedResponse<Product>`   | 400                |
| GET    | `/products/{id}`                                  | `products:read`  | 200 `Product` + `ETag`         | 404                |
| GET    | `/products/search?name=&page=&pageSize=`          | `products:read`  | 200 `PagedResponse<Product>`   | 400                |
| GET    | `/products/stock-level?min=&max=&page=&pageSize=` | `products:read`  | 200 `PagedResponse<Product>`   | 400                |
| POST   | `/products`                                       | `products:write` | 201 `Product` + `Location` + `ETag` | 400           |
| PUT    | `/products/{id}` (optional `If-Match`)            | `products:write` | 200 `Product` + `ETag`         | 400, 404, 409, 412 |
| DELETE | `/products/{id}` (optional `If-Match`)            | `products:write` | 204                            | 404, 409, 412      |
| POST   | `/products/{id}/decrement-stock/{quantity}`       | `stock:write`    | 200 `Product` + `ETag`         | 400, 404, 409      |
| POST   | `/products/{id}/add-to-stock/{quantity}`          | `stock:write`    | 200 `Product` + `ETag`         | 400, 404, 409      |
| GET    | `/categories`                                     | `products:read`  | 200 `Category[]`               | –                  |

Every endpoint may also answer **401** (no or invalid token), **403** (missing permission) or **429** (rate limited,
with `Retry-After`).

**Product:**

```json
{
  "id": 100004, "name": "Office Lens Desk", "description": "Near-to-intermediate lens optimised for screen work.",
  "price": 189.00, "stockQuantity": 5, "categoryId": 1, "categoryName": "Spectacle Lenses",
  "createdAt": "2026-10-09T13:38:11.29+00:00", "updatedAt": "2026-10-09T13:55:02.10+00:00", "version": 812
}
```

**Create / update body:** `name` (required, ≤ 200), `description` (≤ 2000), `price` (0.01–1,000,000, ≤ 2
decimals), `stockQuantity` (0–1,000,000), `categoryId` (must exist). `PUT` replaces all fields.

**Paging:** `page` ≥ 1, `pageSize` 1–100 (default 20). Responses have the shape
`{ items, page, pageSize, totalCount, totalPages }`.

**Errors:** always `application/problem+json` with a stable `errorCode`.

| `errorCode`                       | Status | Meaning |
| --------------------------------- | ------ | ------- |
| `validation_failed`               | 400    | see `errors` (field → messages) |
| `product.not_found`               | 404    | |
| `product.insufficient_stock`      | 409    | includes `requested` and `available` |
| `product.stock_limit_exceeded`    | 409    | stock would exceed 1,000,000 |
| `product.concurrent_modification` | 409    | changed between read and write (no `If-Match` sent) |
| `product.version_mismatch`        | 412    | `If-Match` no longer matches: reload and retry |
| `rate_limited`                    | 429    | see `Retry-After` |
| `http_<status>`                   | any    | framework errors: `http_401`, `http_403`, `http_404` (unknown route), ... |
| `internal_error`                  | 500    | details only in the logs (correlate with `traceId`) |

---

## Security

| Concern        | How |
| -------------- | --- |
| Authentication | Keycloak (OIDC). The browser uses Authorization Code + PKCE (`oidc-client-ts`). The API validates the JWT signature, issuer, audience and lifetime, and never sees passwords ([ADR 0009](docs/adr/0009-authentication-authorization.md)). |
| Authorization  | Permission policies (`products:read`, `stock:write`, `products:write`) mapped from roles (`viewer`, `stock-clerk`, `manager`) in one place. Secure by default: an endpoint without a policy still requires a signed-in user. The UI hides what a role can't do, and the API enforces it. |
| Machine access | The MCP server uses its own service account (client credentials) with only the `stock-clerk` role. |
| Rate limiting  | The gateway limits per IP and the API per user (stricter for writes); both answer `429` + `Retry-After` ([ADR 0010](docs/adr/0010-rate-limiting.md)). |
| Network        | NetworkPolicies: the database and cache are reachable only from the API (and the migration Job); the API and web app only from the gateway. |
| Containers     | Non-root, read-only root filesystem where possible, no capabilities, no privilege escalation, `RuntimeDefault` seccomp, resource limits. |
| Edge           | Security headers (`nosniff`, `X-Frame-Options: DENY`, referrer policy), 1 MB body limit, `server_tokens off`. |
| Errors         | Problem details never include stack traces or internals; serializer messages are hidden. |

---

## Performance and scaling

* **Redis cache:** cache-aside with generation-based invalidation, shared by all replicas, fails open
  ([ADR 0008](docs/adr/0008-redis-cache.md)).
* **nginx gateway:** per-request balancing over the API's headless Service (new replicas are picked up within 5 s),
  edge cache for the bundles, gzip, keep-alive pools ([ADR 0012](docs/adr/0012-gateway.md)).
* **Load test highlights** ([full results](docs/load-test-results.md)):
  * p95 of **3 ms** at 150 req/s with 2 replicas;
  * **0 errors in 255k requests** up to ~1,000 req/s, on a single-node 4-vCPU cluster;
  * **300 concurrent orders for 100 units across replicas → exactly 100 sold, stock 0**;
  * scaling the API to 4 replicas exposed PostgreSQL as the bottleneck. Sizing the connection pool to the database's
    capacity and giving it 2 CPUs removed every error.

---

## MCP (AI assistants)

`backend/ProductManagement.Mcp` is a Model Context Protocol server (official C# SDK, `dotnet new mcpserver`) with
tools such as `search_products`, `list_low_stock`, `add_stock` and `remove_stock`. It calls the REST API like any
other client, so the same authentication, RBAC (it is a `stock-clerk`), validation and rate limits apply
([ADR 0014](docs/adr/0014-mcp-server.md)).

`.mcp.json` (Claude Code) and `.vscode/mcp.json` (VS Code) register it, together with development MCP servers:
Kubernetes (read-only), PostgreSQL (restricted mode) and Playwright. With the app running, ask your assistant
*"Which products are low on stock?"* or *"Record a delivery of 20 units for product 100004."*

---

## Key decisions

Each decision has an ADR in [`docs/adr`](docs/adr/README.md):

| #    | Decision |
| ---- | -------- |
| [0001](docs/adr/0001-clean-architecture.md) | Clean architecture, CQRS-lite (projections for reads, aggregates for writes) |
| [0002](docs/adr/0002-product-id-generation.md) | Six-digit ids from a bounded PostgreSQL sequence, safe across instances |
| [0003](docs/adr/0003-atomic-stock-updates.md) | Stock changes as one conditional UPDATE: no overselling, no retries |
| [0004](docs/adr/0004-optimistic-concurrency.md) | Optimistic concurrency with `xmin`, ETag and If-Match |
| [0005](docs/adr/0005-error-handling.md) | Result pattern and RFC 9457 problem details with stable error codes |
| [0006](docs/adr/0006-postgresql.md) | PostgreSQL, tested against the real engine |
| [0007](docs/adr/0007-frontend-state.md) | Frontend state with signals and `rxResource` |
| [0008](docs/adr/0008-redis-cache.md) | Redis read cache with generation-based invalidation |
| [0009](docs/adr/0009-authentication-authorization.md) | OIDC authentication and permission-based RBAC |
| [0010](docs/adr/0010-rate-limiting.md) | Rate limiting in two layers |
| [0011](docs/adr/0011-kubernetes-deployment.md) | Kubernetes deployment, one manifest per project |
| [0012](docs/adr/0012-gateway.md) | nginx gateway in front of the application |
| [0013](docs/adr/0013-ui-tailwind-cdk.md) | Tailwind CSS + Angular CDK instead of Angular Material |
| [0014](docs/adr/0014-mcp-server.md) | MCP server for AI assistants |
| [0015](docs/adr/0015-load-testing.md) | Load testing with k6 inside the cluster |

---

## Assumptions

* Single currency (EUR in the UI), two decimals, prices from 0.01 to 1,000,000.
* Stock is a whole number from 0 to 1,000,000. A decrement larger than the available stock is **rejected** (409),
  not clamped, so the shortage is never hidden from the caller.
* "Return an OK response" for the stock endpoints means **200 with the updated product**.
* Search is a case-insensitive "contains" on the name. Stock-level bounds are inclusive and optional.
* All list endpoints are paged (default 20, maximum 100).
* Categories are reference data, seeded by the migration and read-only through the API.
* Deletion is a hard delete. Ids are never reused.
* Three roles cover the business (`viewer`, `stock-clerk`, `manager`). Users are managed in the identity provider.

---

## Trade-offs and deviations from common practice

| Choice | Why |
| ------ | --- |
| Stock changes bypass load-modify-save | Atomicity under concurrency. The rule stays in the domain as an expression ([0003](docs/adr/0003-atomic-stock-updates.md)). |
| Nullable value types in request DTOs | A missing field becomes a validation error instead of a silent `0`. |
| No `EnableRetryOnFailure` | The stock endpoints are not idempotent: a blind retry after an ambiguous commit could apply a change twice. |
| `If-Match` optional | Easy to try with Swagger or curl. The UI always sends it, and making it mandatory (428) is one line. |
| Global cache generation (any write invalidates all cached reads) | Simple and race-free across replicas. It costs hit rate under heavy writes, as the load tests measured. |
| In-memory API rate limiter | Fairness per replica with no extra network hop. Global quotas would need Redis; the gateway provides the coarse cluster-wide limit. |
| Real PostgreSQL and Redis in tests (Docker required) | In-memory fakes would hide exactly the behaviour under test. |
| Keycloak in dev mode inside the cluster | One-command local setup. Production uses a managed or production-mode identity provider. |
| Polling instead of push in the UI | Works across replicas with no extra infrastructure, at the cost of up to 15 s of staleness. |

---

## Production readiness and next steps

**In place:**
* health probes, zero-downtime rolling updates, PodDisruptionBudgets, NetworkPolicies;
* hardened non-root containers, immutable image tags;
* JSON logs with trace ids;
* authentication, RBAC, rate limiting, caching, optimistic concurrency, database constraints;
* migrations as a deployment step;
* CI with an end-to-end deployment, and load tests with SLO thresholds.

**Next:**

* **Secrets** from a secret manager (External Secrets, Vault or Sealed Secrets) instead of values in the manifests.
* **TLS** at the edge (cert-manager) with HSTS, and a Content-Security-Policy (which needs Angular's
  `inlineCritical` turned off).
* **Managed PostgreSQL** (or CloudNativePG) with backups, PgBouncer and read replicas; Keycloak in production mode
  with its own database.
* **Observability:** OpenTelemetry traces and metrics (ASP.NET Core, EF Core, Npgsql, Redis), with dashboards and
  alerts on the SLOs used by the load tests.
* A **HorizontalPodAutoscaler** for the API, once the database has headroom.
* **Finer cache invalidation** (per product) and **Idempotency-Key** support for the stock endpoints.
* **GitOps** (Argo CD or Flux) with per-environment overlays (Kustomize or Helm) instead of deploy scripts.

---

## Repository layout

```
.
├── backend/                                  # .NET solution (its own repository in real life)
│   ├── ProductManagement.sln                 # solution folders: Source, Tests, Mcp
│   ├── Dockerfile                            # API image (also runs the migrate command)
│   ├── global.json · Directory.Build.props · Directory.Packages.props · dotnet-tools.json · .editorconfig
│   ├── ProductManagement.Domain/
│   ├── ProductManagement.Application/
│   ├── ProductManagement.Infrastructure/     # EF Core, migrations, Redis cache
│   ├── ProductManagement.Api/                # controllers, security, rate limiting, .http requests
│   ├── ProductManagement.Mcp/                # MCP server
│   ├── ProductManagement.{Domain,Application,Mcp}.UnitTests/
│   ├── ProductManagement.Api.IntegrationTests/ · ProductManagement.Api.AcceptanceTests/
│   ├── ProductManagement.Testing/            # shared test infrastructure
│   └── deploy/                               # kubernetes.yaml + deploy.sh (API, PostgreSQL, Redis, Keycloak)
├── frontend/                                 # Angular workspace (its own repository in real life)
│   ├── Dockerfile · nginx.conf               # web image (static files)
│   ├── gateway/                              # nginx gateway image (Dockerfile, config template)
│   └── deploy/                               # kubernetes.yaml + deploy.sh (web, gateway)
├── load-tests/                               # k6 scenarios + run.sh (runs them as a Kubernetes Job)
├── scripts/                                  # cluster-up / cluster-down (MicroK8s), deploy-all
├── docs/                                     # ADRs, load test results, scaffolding, presentation guide (PT)
├── .mcp.json · .vscode/mcp.json              # MCP servers for Claude Code / VS Code
└── .github/workflows/ci.yml
```

### Tech stack

| Area     | Stack |
| -------- | ----- |
| Backend  | .NET 10, ASP.NET Core, EF Core 10, Npgsql, FluentValidation, StackExchange.Redis, JWT bearer, rate limiting, OpenAPI + Swagger UI |
| Data     | PostgreSQL 18, Redis 8 |
| Identity | Keycloak 26 (OIDC) |
| Frontend | Angular 22 (standalone, zoneless, signals), Tailwind CSS 4, Angular CDK, oidc-client-ts, Vitest, ESLint, Prettier |
| AI       | Model Context Protocol C# SDK |
| Testing  | xUnit v3 (Microsoft Testing Platform), Shouldly, NSubstitute, Testcontainers, Respawn, Reqnroll, k6 |
| Delivery | Docker, Kubernetes (Docker Desktop / MicroK8s), nginx, GitHub Actions |
