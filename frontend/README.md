# Product Management — Web

Angular 22 frontend for the Product Management API: Tailwind CSS + Angular CDK for the UI, sign-in against the
API's token endpoint, role-aware screens. Generated with Angular CLI 22 and extended as described in
[`docs/scaffolding.md`](../docs/scaffolding.md). Architecture and decisions are in the [root README](../README.md) and
[ADR 0007](../docs/adr/0007-frontend-state.md).

## Run

Requires Node 24 LTS (see `.nvmrc`) and the API on http://localhost:5080 (the cluster from the root README plus
`kubectl -n product-management port-forward service/api 5080:8080`, or `dotnet run` in `backend/`).

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
                        browser response cache (ResponseCache + clientCacheInterceptor), notifications
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
| `npm run e2e`              | Playwright against the running system (default http://localhost:8080), desktop and mobile |
| `npm run audit:lighthouse` | Lighthouse user flow, signed in (`FORM_FACTOR=desktop` for desktop); report in `e2e/reports/` |

## Deployment

* `Dockerfile` + `nginx.conf`: the **web** image (static files only).
* `gateway/`: the **gateway** image (nginx in front of everything: `/api` with a 1 s microcache, static files,
  rate limiting, edge cache, Content-Security-Policy and the other security headers in `security-headers.conf`).
  It autoscales (HPA, 2–6 replicas).
* `deploy/kubernetes.yaml` + `deploy/deploy.sh`: deploys both to Kubernetes, independently of the backend.
