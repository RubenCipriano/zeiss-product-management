# Product Management — Web

Angular 22 frontend for the Product Management API. Generated with Angular CLI 22 and extended as described in
[`docs/scaffolding.md`](../docs/scaffolding.md). Architecture and decisions are in the [root README](../README.md) and
[ADR 0007](../docs/adr/0007-frontend-state.md).

## Run

Requires Node 24 LTS (see `.nvmrc`) and the API running on http://localhost:5080
(`docker compose up -d db` and then `dotnet run --project ../backend/src/ProductManagement.Api`).

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
  core/          app-wide services: API base URL, ApiError + HTTP interceptor, notifications
  shared/        reusable pieces: form validators/messages, PagedResponse, ConfirmDialog
  features/products/
    data-access/ ProductsApi, CategoriesApi (HTTP), ProductsStore (signals + rxResource), models
    product-list/, product-form-dialog/, stock-adjust-dialog/, stock-status/
src/testing/     test fixtures
```

In Docker the app is served by unprivileged nginx (`nginx.conf`), which also reverse-proxies `/api` to the API
container, so the browser always talks to a single origin.
