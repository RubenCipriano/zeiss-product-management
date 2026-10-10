# Product Management — Web

The Angular 22 frontend of the Product Management system (Tailwind CSS and Angular CDK, sign-in against the API's
token endpoint, role-aware screens) and the nginx **gateway** in front of the whole application. This repository
holds the code, the unit and end-to-end tests, the two images and their CI pipeline. The Kubernetes manifests and the
environments are in the [deploy](https://gitlab.com/zeiss/deploy) repository; the API in
[backend](https://gitlab.com/zeiss/backend).

## Run locally

Requires Node 24 LTS (`.nvmrc`) and an API to talk to.

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

The whole system (gateway, web app, API, PostgreSQL, Redis, logs) on Docker Desktop's Kubernetes: clone the three
repositories side by side and follow the deploy repository's README (`scripts/infra.sh up`, then
`scripts/deploy.sh local`, which builds this folder's two images) → http://localhost:8080.

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
kubectl -n infra port-forward svc/loki 3100:3100 &         # optional: the browser-errors test reads Loki (LOKI_URL)
BASE_URL=https://staging.products.example.com \
E2E_VIEWER_SECRET=… E2E_CLERK_SECRET=… E2E_MANAGER_SECRET=… npm run e2e     # against an environment
```

Against an environment the consumers `viewer`, `clerk` and `manager` must be registered in its API, with their
secrets in `E2E_<ROLE>_SECRET`, and paging needs more than three pages of products (staging seeds the sample
catalogue). The tests clean up what they create; the browser-errors test is skipped without Loki.

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
