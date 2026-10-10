# Product Management — Web

The Angular 22 frontend of the Product Management system (Tailwind CSS and Angular CDK, sign-in against the API's
token endpoint, role-aware screens) and the nginx **gateway** in front of the whole application. This repository
holds the code, the unit and end-to-end tests, the two images and their CI pipeline. The Kubernetes manifests and the
environments are in the [deploy](https://gitlab.com/zeiss/deploy) repository; the API in
[backend](https://gitlab.com/zeiss/backend).

## What it does

The assessment asked for an Angular app that consumes the API and reflects its changes:

* **Products page:** a paged table with each product's stock and status (in stock, low, out); search by name
  (partial); filter by stock range; summary cards (total, low stock, out of stock).
* **Changes:** create, edit and delete (with confirmation); add or remove stock in one click with a chosen step
  (1, 5, 10, 25 or 100) or by an exact amount; each product's stock history.
* **Reflects the API:** the page reloads after every change, refreshes itself every 15 seconds so changes made by
  others appear, and shows when someone else edited a product meanwhile (`412`) instead of overwriting it.
* **Sign-in and roles:** a viewer only reads, a stock clerk changes stock, a manager does everything; buttons the
  user may not use are not shown.

Added for a production feel: a browser cache (the cached page appears at once, then the fresh one replaces it), the
next page loaded ahead of time, a loading bar instead of an emptied table, accessible dialogs and menus (Angular CDK),
a strict Content-Security-Policy, and the browser's errors reported to the logs (no personal data). The **gateway**
(nginx) is the single entry point: it serves the app, routes `/api` to the API replicas, checks the token before a
request reaches the API, rate-limits per client and keeps read answers for one second.

## Design decisions

* **Standalone components and signals** (Angular 22's defaults, no NgModules, no zone.js): the screen updates because
  a value changed, which keeps updates predictable.
* **Three layers per feature:** `data-access/` talks to the API and holds the page's state (`ProductsApi`,
  `ProductsStore`), `feature/` decides what happens when the user acts (`ProductActions`, the dialogs), `ui/` only
  displays and emits events. A table never calls the API.
* **One idempotency key per click:** a retried or double-clicked stock change is applied once by the API.
* **Tailwind + Angular CDK** instead of a component library: small bundle (≈ 94 kB transferred on first load) and
  accessible behaviour (focus trap, keyboard, screen readers) without fighting a theme.
* **Same origin through the gateway:** no CORS, one CSP, and the token check and caching in one place for every
  client.

## Run locally

Requires Node 24 LTS (`.nvmrc`) and an API to talk to. Node 24, per platform:

| | Node 24 |
| --- | --- |
| macOS | `brew install node@24`, or nvm: `nvm install` (reads `.nvmrc`) |
| Windows | `winget install CoreyButler.NVMforWindows`, then `nvm install 24` and `nvm use 24` (or the 24.x installer from https://nodejs.org) |
| Linux (Ubuntu) | nvm (https://github.com/nvm-sh/nvm): `nvm install`; or NodeSource: `curl -fsSL https://deb.nodesource.com/setup_24.x \| sudo -E bash - && sudo apt install -y nodejs` |

The commands are the same everywhere (on Windows in PowerShell, Git Bash or WSL 2):

```bash
npm ci
npm start                                                  # http://localhost:4200, live reload
```

The dev server proxies `/api` ([`proxy.conf.mjs`](proxy.conf.mjs)) to `API_URL`, by default http://localhost:5080:

| The API | How |
| --- | --- |
| on this machine | `dotnet run --project ProductManagement.Api` in the backend repository (its README), then `npm start` |
| the local cluster's | `kubectl -n product-management port-forward deployment/api 5080:8080`, then `npm start`; or through its gateway: `API_URL=http://localhost:8080 npm start` |
| an environment's | `API_URL=https://staging.products.example.com npm start` (the gateway serves the API under the same `/api`) |

Sign in with a consumer of that API: locally `viewer`/`viewer`, `clerk`/`clerk` or `manager`/`manager`.

The whole system (gateway, web app, API, PostgreSQL, Redis, logs) on Docker Desktop's Kubernetes (macOS, Windows) or
a k3s on an Ubuntu server: clone the three repositories side by side and follow the deploy repository's README, "Where
it runs" (`scripts/infra.sh up`, then `scripts/deploy.sh local`, which builds this folder's two images) →
http://localhost:8080 (on the Ubuntu server also http://&lt;server-ip&gt;:8080).

## Scripts

| Script                     | Purpose                                                                 |
| -------------------------- | ----------------------------------------------------------------------- |
| `npm start`                | dev server with live reload (`API_URL`: where `/api` goes)              |
| `npm test`                 | unit and component tests (Vitest), watch mode                           |
| `npm run test:ci`          | the same, single run                                                    |
| `npm run lint`             | ESLint (angular-eslint)                                                 |
| `npm run format`           | Prettier (write); `npm run format:check` to check                       |
| `npm run build`            | production build → `dist/`                                              |
| `npm run e2e`              | Playwright end-to-end tests against a running system (below)            |
| `npm run audit:lighthouse` | Lighthouse user flow, signed in (`FORM_FACTOR=desktop` for desktop); report in `e2e/reports/` |

## End-to-end tests

`e2e/` drives a real browser (desktop and phone) through the real gateway, API, Redis and PostgreSQL, so it needs a
running system; it is not a CI job. By default it targets the local cluster (http://localhost:8080):

```bash
npx playwright install chromium                            # once, if Chrome is not installed (then PLAYWRIGHT_CHANNEL=chromium)
npm run e2e                                                # against http://localhost:8080 with the local demo accounts
BASE_URL=http://<server-ip>:8080 npm run e2e               # against the deploy repository's k3s on an Ubuntu server
kubectl -n infra port-forward svc/loki 3100:3100 &         # optional: the browser-errors test reads Loki (LOKI_URL)
BASE_URL=https://staging.products.example.com \
E2E_VIEWER_SECRET=… E2E_CLERK_SECRET=… E2E_MANAGER_SECRET=… npm run e2e     # against an environment
```

Against an environment the consumers `viewer`, `clerk` and `manager` must be registered in its API, with their
secrets in `E2E_<ROLE>_SECRET`. The paging test needs more than three pages of products (over 60; a fresh database
has only the sample catalogue's 24, so add some first). The tests clean up what they create; the browser-errors test
is skipped without Loki (on a k3s server, port-forward it from a laptop with a copy of the server's kubeconfig, as the
deploy repository's README describes).

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
gateway/                the gateway image
ci/                     the script of the pipeline's release jobs
```

Dependency rules, so each piece has one reason to change:
* `ui/` components only take `input()`s and emit `output()`s: no services, no store. Reusable and trivial to test.
* `feature/` orchestrates: the page wires the store and `ProductActions` to the `ui/` components; `ProductActions`
  opens dialogs and reports results.
* `data-access/` knows HTTP and state, nothing about components.
* Across folders, import through the folder's `index.ts`; inside a folder, import files directly.

Standalone components throughout (no NgModules, the Angular default since v19): each component imports exactly what
it uses.

## The images

* `Dockerfile` + `nginx.conf`: the **web** image, the static files only
  (`docker build --build-arg APP_VERSION=dev -t product-management/web .`).
* `gateway/`: the **gateway** image (`docker build -t product-management/gateway gateway`), nginx in front of
  everything: `/api` to the API replicas (per-request balancing, token verification at the edge, rate limiting, a 1 s
  microcache), the static files with an edge cache, Content-Security-Policy and the other security headers
  (`security-headers.conf`), and `POST /collect`, the browser's error reports, forwarded to the log collector. Its
  settings are environment variables (`default.conf.template`, rendered at startup): `API_UPSTREAM`, `JWKS_URL`,
  `COLLECTOR_UPSTREAM` (`""`: `/collect` answers 204 and drops the report, `gateway/collector.sh`),
  `GATEWAY_RATE_LIMIT_RPS` / `_BURST`, `GATEWAY_TRUSTED_PROXIES`, `JWT_ISSUER`, `JWT_AUDIENCE`. The deployed values
  are in the deploy repository (`apps/frontend/gateway.yaml`, `environments/<environment>/gateway.yaml`).

## CI (GitLab)

[`.gitlab-ci.yml`](.gitlab-ci.yml). The pipeline builds, checks and releases the images; it never applies Kubernetes
manifests. An environment runs the image tags in the deploy repository's
`environments/<environment>/kustomization.yaml`, and the deploy repository's pipeline applies them.

| Job | When | Does |
| --- | --- | --- |
| `test:frontend` | every pipeline | lint, format check, unit tests (JUnit report), production build |
| `build:web`, `build:gateway` | every pipeline | build and push `registry.gitlab.com/zeiss/frontend/{web,gateway}:<sha>` (BuildKit, registry layer cache) |
| `scan:web`, `scan:gateway` | every pipeline | Trivy; fail on fixable critical vulnerabilities |
| `release:staging` | `main`, `STAGING_ENABLED` | [`ci/release.sh`](ci/release.sh): commits `frontend: web, gateway <sha>` (both `newTag`s) to the deploy repository's `main` |
| `release:production` | `main`, `PRODUCTION_ENABLED`, **manual** | pushes a branch to the deploy repository and opens the merge request "Release web, gateway `<sha>` to production"; merging it releases |

CI/CD variables:

| Variable | Scope | What |
| --- | --- | --- |
| `STAGING_ENABLED` | project or group | `"true"` creates `release:staging` on `main` |
| `PRODUCTION_ENABLED` | project or group | `"true"` creates `release:production` (manual); needs `STAGING_ENABLED` too |
| `DEPLOY_REPO_TOKEN` | project; masked, protected | a project access token of the deploy repository with the role Maintainer and the scope `write_repository` |

Nothing else: the cluster pulls the images with the zeiss group's deploy token, which the deploy repository's
pipeline stores in the cluster. Until `STAGING_ENABLED` is set the pipeline stops, green, after scan. Rollback: revert
the release commit (or merge) in the deploy repository.
