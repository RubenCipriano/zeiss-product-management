# Product Management — Web

Angular 22 frontend for the Product Management API: Tailwind CSS + Angular CDK for the UI, sign-in against the
API's token endpoint, role-aware screens. Generated with Angular CLI 22. This folder is self-contained, its own
repository ([gitlab.com/zeiss/frontend](https://gitlab.com/zeiss/frontend)): the web app, the nginx **gateway** in
front of the whole application, their Kubernetes manifests, the deploy script, the end-to-end tests and the GitLab
pipeline. Architecture and decisions are in the
[guide](../docs/README.md) ([chapter 7](../docs/guide/07-frontend.md)) and [ADR 0007](../docs/adr/0007-frontend-state.md).

## Run

Requires Node 24 LTS (see `.nvmrc`) and the API on http://localhost:5080: the API deployed to the cluster by the
backend repository plus `kubectl -n product-management port-forward deployment/api 5080:8080`, or `dotnet run` in the
backend repository.

```bash
npm ci
npm start          # http://localhost:4200, /api is proxied to http://localhost:5080 (proxy.conf.json)
```

## Scripts

| Script                 | Purpose                         |
| ---------------------- | ------------------------------- |
| `npm start`            | Dev server with live reload     |
| `npm test`             | Vitest in watch mode            |
| `npm run test:ci`      | Vitest, single run              |
| `npm run lint`         | ESLint (angular-eslint)         |
| `npm run format`       | Prettier (write)                |
| `npm run format:check` | Prettier (check)                |
| `npm run build`        | Production build → `dist/`      |

## Structure

```
src/app/
  core/                 app-wide: auth (session, guard, interceptor), API base URL, ApiError + interceptor,
                        browser response cache (ResponseCache + clientCacheInterceptor), notifications,
                        browser error reporting to Loki (ReportingErrorHandler + Grafana Faro, loaded lazily)
  shared/
    ui/                 presentational building blocks: AppDialog, ConfirmDialog, icon, paginator, progress bar, toasts
    util/               inject-functions ("hooks"): autoRefresh()
    pipes/  forms/  models/
  features/auth/login/  sign-in page
  features/products/
    data-access/        ProductsApi, CategoriesApi (HTTP), ProductsStore (signals + rxResource), models
    feature/            ProductsPage (container), ProductActions (what the user can do), the dialogs
    ui/                 presentational: InventorySummary, ProductFilters, ProductTable, StockStatus, pipes
src/testing/            test fixtures
e2e/                    Playwright end-to-end tests and the Lighthouse user flow
```

Dependency rules, so each piece has one reason to change:
* `ui/` components only take `input()`s and emit `output()`s: no services, no store. Reusable and trivial to test.
* `feature/` orchestrates: the page wires the store and `ProductActions` to the `ui/` components; `ProductActions`
  opens dialogs and reports results.
* `data-access/` knows HTTP and state, nothing about components.
* Across folders, import through the folder's `index.ts`; inside a folder, import files directly.

Standalone components throughout (no NgModules, the Angular default since v19): each component imports exactly what
it uses.

## Quality checks

| Script                     | What it checks                                                                 |
| -------------------------- | ------------------------------------------------------------------------------ |
| `npm run test:ci`          | unit and component tests (Vitest)                                              |
| `npm run e2e`              | Playwright against the running system (default http://localhost:8080), desktop and mobile; the browser-errors test also reads Loki (`LOKI_URL`, default http://localhost:3100) and is skipped without it |
| `npm run audit:lighthouse` | Lighthouse user flow, signed in (`FORM_FACTOR=desktop` for desktop); report in `e2e/reports/` |


`npm run e2e` takes `BASE_URL` (default http://localhost:8080) and, against an environment other than the local one,
`E2E_VIEWER_SECRET`, `E2E_CLERK_SECRET` and `E2E_MANAGER_SECRET` (the demo accounts' secrets otherwise).

## Deployment

Two images, deployed together to Kubernetes:

* `Dockerfile` + `nginx.conf`: the **web** image (the static files only).
* `gateway/`: the **gateway** image, nginx in front of everything: `/api` to the API replicas (per-request balancing,
  token verification at the edge, rate limiting, a 1 s microcache), the static files with an edge cache,
  Content-Security-Policy and the other security headers (`security-headers.conf`), and `POST /collect`, the
  browser's error reports, forwarded to the log collector in the cluster. Its settings are environment variables
  (`default.conf.template` is rendered at startup); `gateway/collector.sh` (run as
  `/docker-entrypoint.d/25-collector.sh`) decides what `/collect` does: forward to `COLLECTOR_UPSTREAM`, or, when it
  is empty, answer 204 and drop the report. It autoscales (HPA, 2–6 replicas).

### Prerequisites

Docker and `kubectl` with a cluster (Docker Desktop's Kubernetes, context `docker-desktop`). Node is not needed to
deploy: the images are built in Docker.

**What it expects from the other parts:** the API deployed to the same cluster by the backend repository (its
Service `api` in the namespace `product-management`) and, optionally, the cluster's log collector deployed by the
infra repository (`infra.sh up logs`).

### Deploy

```bash
deploy/deploy.sh                  # build the web and gateway images, apply the local overlay, wait → http://localhost:8080
LOGS=off deploy/deploy.sh         # the same, without a log collector: /collect answers 204
```

[`deploy/deploy.sh`](deploy/deploy.sh) deploys to the current `kubectl` context from
`deploy/k8s/overlays/$ENVIRONMENT` (`local` by default). Locally it builds both images, tagged with the commit (plus
`-dirty-<timestamp>` with uncommitted changes), which Docker Desktop's Kubernetes uses straight from the local
Docker engine (any other context needs `WEB_IMAGE` and `GATEWAY_IMAGE`, images from a registry), renders the overlay with the
images pinned into `deploy/k8s/.build/<environment>/` (not committed), applies it and waits for both rollouts. On
Docker Desktop the gateway is a `LoadBalancer` Service on http://localhost:8080; elsewhere
`kubectl -n product-management port-forward service/gateway 8080:8080`.

| Variable | Effect |
| --- | --- |
| `ENVIRONMENT` | `local` (default), `staging` or `production`: the overlay |
| `WEB_IMAGE`, `GATEWAY_IMAGE` | images already built and pushed (`registry/name:tag` or `name@sha256:…`): no local build |
| `LOGS=off` | no log collector: the gateway's `COLLECTOR_UPSTREAM` becomes `""` |

### Configuration

* `deploy/k8s/base/`: the namespace, `web.yaml` (Deployment, Service, PDB, NetworkPolicy: only the gateway may
  connect) and `gateway.yaml` (Deployment, Service, PDB, HPA). The gateway's settings are environment variables in
  its Deployment: `GATEWAY_RATE_LIMIT_RPS` / `GATEWAY_RATE_LIMIT_BURST` (per client IP), `GATEWAY_TRUSTED_PROXIES`,
  `JWT_ISSUER`, `JWT_AUDIENCE`.
* `deploy/k8s/overlays/<environment>/gateway.yaml`: where the other parts are.

  | Variable | Value (every environment) | What |
  | --- | --- | --- |
  | `API_UPSTREAM` | `api.product-management.svc.cluster.local:8080` | the API's headless Service: one DNS record per API pod |
  | `JWKS_URL` | `http://api.product-management.svc.cluster.local:8080/.well-known/jwks.json` | the API's public keys, to verify tokens |
  | `COLLECTOR_UPSTREAM` | `alloy-faro.observability.svc.cluster.local:12347` | the log collector; `""` = no collector |

  To use an API elsewhere, put its `host:port` in `API_UPSTREAM` and its key set's URL in `JWKS_URL`, and deploy
  again. Staging and production also set `GATEWAY_TRUSTED_PROXIES=10.42.0.0/16` (the Ingress controller's pod
  network) and add `ingress.yaml`: HTTPS for `staging.products.example.com` / `products.example.com` with a
  cert-manager certificate (replace the `example.*` hosts with the real ones). Locally the overlay makes the gateway's
  Service a `LoadBalancer`.
* No Secret: the web app and the gateway need none.

Check the manifests as CI does:

```bash
deploy/ci/validate-manifests.sh                     # needs kubectl, kubeconform and yq
docker run --rm -v "$PWD:/work:ro" -w /work alpine/k8s:1.34.11 deploy/ci/validate-manifests.sh
```

### CI/CD (GitLab)

[`.gitlab-ci.yml`](.gitlab-ci.yml): test (`test:frontend`: lint, format, unit tests, build; `test:manifests`) →
build (`build:web`, `build:gateway`) → scan (`scan:web`, `scan:gateway`, Trivy) → staging (`deploy:staging`:
`kubectl config use-context "$KUBE_CONTEXT"` and `deploy/deploy.sh`) → verify (`verify:e2e`, Playwright against
`STAGING_URL`) → production (`deploy:production`, manual).

CI/CD variables, scoped to the `staging` and `production` environments:

| Variable | What |
| --- | --- |
| `KUBE_CONTEXT` | `<agent configuration project path>:<agent name>` (the GitLab agent for Kubernetes) |
| `E2E_VIEWER_SECRET`, `E2E_CLERK_SECRET`, `E2E_MANAGER_SECRET` | staging only, masked: the secrets of the consumers `viewer`, `clerk` and `manager` registered in staging's API (the backend's `API_CONSUMERS`) |

`STAGING_URL` and `PRODUCTION_URL` default to the example hosts in `.gitlab-ci.yml`; override them in the project.
Deploy the backend to an environment before the frontend's first deployment there. Operations and rollbacks:
[chapter 9](../docs/guide/09-deployment-and-operations.md); full setup: [chapter 12](../docs/guide/12-from-zero.md).
