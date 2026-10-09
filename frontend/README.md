# Product Management — Web

Angular 22 frontend for the Product Management API: Tailwind CSS + Angular CDK for the UI, OIDC sign-in
(Keycloak, `oidc-client-ts`) with role-aware screens. Generated with Angular CLI 22 and extended as described in
[`docs/scaffolding.md`](../docs/scaffolding.md). Architecture and decisions are in the [root README](../README.md) and
[ADR 0007](../docs/adr/0007-frontend-state.md).

## Run

Requires Node 24 LTS (see `.nvmrc`), the API on http://localhost:5080 and Keycloak on http://localhost:8080
(the cluster from the root README, plus `kubectl -n product-management port-forward service/api 5080:8080`).

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
  core/          app-wide services: auth (OIDC, guard, interceptor), API base URL, ApiError + interceptor, notifications
  shared/        reusable pieces: form validators/messages, PagedResponse, icon, paginator, toasts, ConfirmDialog
  features/products/
    data-access/ ProductsApi, CategoriesApi (HTTP), ProductsStore (signals + rxResource), models
    product-list/, product-form-dialog/, stock-adjust-dialog/, stock-status/
src/testing/     test fixtures
```

## Deployment

* `Dockerfile` + `nginx.conf`: the **web** image (static files only).
* `gateway/`: the **gateway** image (nginx in front of everything: `/api`, `/auth`, static files, rate limiting,
  edge cache).
* `deploy/kubernetes.yaml` + `deploy/deploy.sh`: deploys both to Kubernetes, independently of the backend.
