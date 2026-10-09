# Product Management

A product and stock management system: a **.NET 10 (ASP.NET Core) Web API** with **EF Core** and **PostgreSQL**, and an
**Angular 22** frontend.

![Product list](docs/images/product-list.png)

- [Quick start](#quick-start)
- [Running for development](#running-for-development)
- [Tests](#tests)
- [Requirements checklist](#requirements-checklist)
- [Architecture](#architecture)
- [API reference](#api-reference)
- [Key decisions](#key-decisions)
- [Assumptions](#assumptions)
- [Trade-offs and deviations from common practice](#trade-offs-and-deviations-from-common-practice)
- [Production readiness and next steps](#production-readiness-and-next-steps)
- [Repository layout](#repository-layout)

> **A note on ".NET Core":** since .NET 5, Microsoft dropped "Core" from the runtime's name. **.NET 10 is the current
> release of .NET Core** (the cross-platform runtime, not the Windows-only .NET Framework) and the current LTS
> (supported until November 2028). The web and data layers keep the "Core" name: **ASP.NET Core** and **Entity
> Framework Core**.

---

## Quick start

**Prerequisite:** Docker Desktop (or any Docker engine with Compose v2).

```bash
docker compose up --build
```

| What                 | URL                                  |
| -------------------- | ------------------------------------ |
| Web application      | http://localhost:8080                |
| API (Swagger UI)     | http://localhost:5080/swagger        |
| OpenAPI document     | http://localhost:5080/openapi/v1.json |
| Health (liveness)    | http://localhost:5080/health/live    |
| Health (readiness)   | http://localhost:5080/health/ready   |

On first start the API applies the EF Core migrations (schema, sequence, constraints, indexes, categories) and seeds
24 sample products. Data is kept in the `db-data` volume; `docker compose down -v` resets it.

Ports can be changed with environment variables (`WEB_PORT`, `API_PORT`, `POSTGRES_PORT`), e.g.
`API_PORT=5081 docker compose up --build`.

---

## Running for development

| Tool         | Version                                   |
| ------------ | ----------------------------------------- |
| .NET SDK     | 10.0.401+ (pinned in `backend/global.json`) |
| Node.js      | 24 LTS (`frontend/.nvmrc`; 22.22+ also supported) |
| Docker       | for PostgreSQL and the integration tests  |

### 1. Database

```bash
docker compose up -d db
```

### 2. API

**Visual Studio 2026 (the release that supports .NET 10) or JetBrains Rider:** open `backend/ProductManagement.sln`, set
`ProductManagement.Api` as the startup project and press **F5**. Swagger opens at http://localhost:5080/swagger.
`ProductManagement.Api.http` contains a ready-made request for every endpoint.

**CLI:**

```bash
cd backend
dotnet tool restore      # restores the pinned dotnet-ef tool
dotnet run --project src/ProductManagement.Api
```

In the `Development` environment the API applies migrations and seeds sample data on startup
(`appsettings.Development.json`). To apply migrations manually instead:

```bash
dotnet ef database update --project src/ProductManagement.Infrastructure --startup-project src/ProductManagement.Api
```

### 3. Frontend

```bash
cd frontend
npm ci
npm start                # http://localhost:4200, proxies /api to http://localhost:5080
```

| Script                 | Purpose                                 |
| ---------------------- | --------------------------------------- |
| `npm start`            | Dev server with live reload             |
| `npm test`             | Unit tests (Vitest), watch mode         |
| `npm run test:ci`      | Unit tests, single run                  |
| `npm run lint`         | ESLint (angular-eslint)                 |
| `npm run format:check` | Prettier                                |
| `npm run build`        | Production build                        |

---

## Tests

```bash
cd backend && dotnet test --solution ProductManagement.sln     # 145 tests, needs Docker running
cd frontend && npm run test:ci                                   # 37 tests
```

The tests are chosen to build confidence in behaviour that matters, not to chase a coverage number.

| Suite                                     | Count | What it proves                                                                                      |
| ----------------------------------------- | ----: | --------------------------------------------------------------------------------------------------- |
| `Domain.UnitTests`                        | 32    | Invariants (six-digit ids, price precision, stock range), an update with invalid data leaves no partial changes, and the stock rule at its edges (including deltas that would overflow naive arithmetic). |
| `Application.UnitTests`                   | 53    | Use-case orchestration with substitutes: validation runs before anything else, invalid requests consume no id, version checks, concurrency conflicts mapped to 409/412, stock errors carry the available quantity. Validators at their boundaries. |
| `Api.IntegrationTests`                    | 50    | Every endpoint over real HTTP against **real PostgreSQL** (Testcontainers): status codes, headers (`Location`, `ETag`), the problem-details contract, LIKE wildcards treated literally, paging. **Concurrency:** 60 parallel orders on 25 units → exactly 25 succeed and stock ends at 0; mixed parallel +/− lose no update; **two API instances** creating products concurrently never produce a duplicate id; a stale `If-Match` gets 412. |
| `Api.AcceptanceTests` (BDD, Reqnroll)     | 10    | Gherkin scenarios written from the user's point of view (`Features/*.feature`): stock management, catalogue search, low-stock lookup, editing a product someone else changed. |
| Frontend (Vitest)                         | 37    | HTTP contract of the API client (URLs, params, `If-Match`), error normalisation, store behaviour (filter resets the page, no duplicate requests, immediate update then refetch), dialogs (validation, server field errors, 412 conflict + reload), list rendering, empty and error states. |

The integration and acceptance tests start one PostgreSQL container per run. Respawn resets the data between tests
and keeps reference data and migration history. Tests run against the same engine as production because the
behaviour under test (sequences, row locks, `xmin`, `ILIKE`) is engine-specific.

CI (`.github/workflows/ci.yml`) runs build (warnings as errors), all backend and frontend tests, lint and format checks,
and the container builds.

---

## Requirements checklist

| Requirement (assessment PDF)                                   | Where / how                                                                                            |
| -------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| 5 CRUD endpoints                                               | `ProductsController`: `GET/POST /api/products`, `GET/PUT/DELETE /api/products/{id}`                      |
| `POST /api/products/{id}/decrement-stock/{quantity}` → OK       | Atomic conditional UPDATE, returns 200 + updated product ([ADR 0003](docs/adr/0003-atomic-stock-updates.md)) |
| `POST /api/products/{id}/add-to-stock/{quantity}` → OK          | Same mechanism, upper bound enforced                                                                   |
| `GET /api/products/search?name=` (partial match)               | Case-insensitive `ILIKE '%…%'` (wildcards escaped), trigram index                                      |
| `GET /api/products/stock-level?min=&max=`                      | Inclusive range, either bound optional, ordered by stock                                               |
| Unique 6-digit id, no duplicates across instances              | PostgreSQL sequence `100000–999999 NO CYCLE` ([ADR 0002](docs/adr/0002-product-id-generation.md)), multi-instance test |
| Validation of create/update                                    | FluentValidation + domain guards + DB check constraints ([ADR 0005](docs/adr/0005-error-handling.md)) |
| Stock included in every list response                          | `stockQuantity` on every `ProductResponse`                                                             |
| EF Core code-first migrations                                  | `Infrastructure/Persistence/Migrations/…_InitialCreate.cs` (generated by `dotnet ef`)                 |
| Database seeding                                               | Categories via `HasData` (in the migration), sample products via `UseSeeding`/`UseAsyncSeeding`       |
| Angular frontend consuming the API, reflecting changes         | `frontend/`: every endpoint used, refetch after mutations + background refresh ([ADR 0007](docs/adr/0007-frontend-state.md)) |
| Unit tests for various scenarios                               | 85 backend unit tests + 37 frontend tests                                                               |
| *Optional:* BDD tests                                          | `ProductManagement.Api.AcceptanceTests` (Reqnroll / Gherkin)                                          |
| Documentation for running locally                              | This README, [`docs/scaffolding.md`](docs/scaffolding.md), [`docs/adr/`](docs/adr/README.md)           |

---

## Architecture

```
                    ┌────────────────────────── backend ────────────────────────────┐
 Angular SPA ──/api──►  Api  ──►  Application  ──►  Domain                          │
 (nginx / ng serve)  │   │            ▲                                              │
                     │   └──────►  Infrastructure ──► EF Core ──► PostgreSQL         │
                     └───────────────────────────────────────────────────────────────┘
```

| Project                                | Responsibility                                                                                         |
| -------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| `ProductManagement.Domain`             | `Product` aggregate and `Category`; invariants; `ProductRules` (single source for limits); the stock rule as an expression. No dependencies. |
| `ProductManagement.Application`        | Use cases: `ProductQueryService` (reads) and `ProductCommandService` (writes). Request/response contracts, FluentValidation validators, `Result`/`Error`, paging, ports (`IProductRepository`, `IProductQueries`, `IProductIdGenerator`, `IUnitOfWork`). |
| `ProductManagement.Infrastructure`     | `ProductManagementDbContext`, entity configurations, migrations, repositories/queries, sequence-based id generator, seeding, health check. |
| `ProductManagement.Api`                | Thin controllers, HTTP mapping (status codes, ETag/If-Match, problem details), global exception handler, OpenAPI/Swagger, CORS, health endpoints. |

A write request, end to end (`PUT /api/products/{id}` with `If-Match`):

1. The **controller** parses `If-Match` and calls `IProductCommandService.UpdateAsync`. It contains no logic beyond HTTP.
2. The **service** validates the request (FluentValidation), loads the tracked aggregate, compares versions (412 if stale),
   checks the category, then calls `product.Update(...)`.
3. The **domain** enforces invariants: all fields are validated before any is assigned.
4. The **unit of work** saves. EF Core puts the loaded `xmin` in the UPDATE's `WHERE`, so a concurrent change raises a
   conflict, which becomes `ConcurrencyConflictException` (no EF types leak upwards) and then 412/409.
5. The **query side** re-reads the product as a projection, and the controller returns it with a fresh `ETag`.

EF Core usage, in brief:

* **Reads** are `AsNoTracking()` projections straight to response records (one SQL statement with a join; no entity
  materialisation or change tracking). Count + page for paging.
* **Writes** load tracked aggregates only when a load-modify-save is needed (update, delete). Stock changes use
  `ExecuteUpdateAsync` (one statement, no tracking).
* Everything is **async** and honours `CancellationToken` (an aborted HTTP request cancels its SQL).
* **`AddDbContextPool`** reuses context instances under load.
* **Constraints in the database too:** primary key, FK (restrict), check constraints mirroring `ProductRules`, unique
  category names, indexes on `stock_quantity` and a trigram GIN index on `name`.
* **Seeding is split by purpose:** reference data (`categories`) ships in the migration via `HasData`. Sample products
  come from `UseSeeding`/`UseAsyncSeeding`: idempotent, optional (`Database:SeedSampleData`), and they use the same id
  sequence as real data.
* **Migrations at startup** are opt-in (`Database:ApplyMigrationsOnStartup`). EF Core takes a database-wide lock while
  migrating, so several instances starting together are safe.

Frontend structure (`frontend/src/app`):

```
core/        config (API base URL), http (ApiError + interceptor), notifications
shared/      forms (validators, messages), models (PagedResponse), ui (ConfirmDialog)
features/
  products/
    data-access/   ProductsApi, CategoriesApi, ProductsStore (signals + rxResource), models
    product-list/  page: filters, table, paging, quick stock buttons
    product-form-dialog/, stock-adjust-dialog/, stock-status/
```

---

## API reference

Base path `/api`. JSON uses camelCase. The complete schema is in Swagger.

| Method | Path                                             | Success                     | Errors               |
| ------ | ------------------------------------------------ | --------------------------- | -------------------- |
| GET    | `/products?page=1&pageSize=20`                   | 200 `PagedResponse<Product>` | 400                  |
| GET    | `/products/{id}`                                 | 200 `Product` + `ETag`       | 404                  |
| GET    | `/products/search?name=lens&page=&pageSize=`     | 200 `PagedResponse<Product>` | 400 (missing name)   |
| GET    | `/products/stock-level?min=0&max=10&page=&pageSize=` | 200 `PagedResponse<Product>` | 400 (negative, min > max) |
| POST   | `/products`                                      | 201 `Product` + `Location` + `ETag` | 400          |
| PUT    | `/products/{id}` (optional `If-Match`)           | 200 `Product` + `ETag`       | 400, 404, 409, 412   |
| DELETE | `/products/{id}` (optional `If-Match`)           | 204                         | 404, 409, 412        |
| POST   | `/products/{id}/decrement-stock/{quantity}`      | 200 `Product` + `ETag`       | 400, 404, 409        |
| POST   | `/products/{id}/add-to-stock/{quantity}`         | 200 `Product` + `ETag`       | 400, 404, 409        |
| GET    | `/categories`                                    | 200 `Category[]`             | —                    |

**Product**

```json
{
  "id": 100004,
  "name": "Office Lens Desk",
  "description": "Near-to-intermediate lens optimised for screen work.",
  "price": 189.00,
  "stockQuantity": 5,
  "categoryId": 1,
  "categoryName": "Spectacle Lenses",
  "createdAt": "2026-10-09T13:38:11.29+00:00",
  "updatedAt": "2026-10-09T13:55:02.10+00:00",
  "version": 812
}
```

**Create / update body**: `name` (required, ≤ 200), `description` (optional, ≤ 2000), `price` (0.01–1,000,000, ≤ 2
decimals), `stockQuantity` (0–1,000,000, integer), `categoryId` (existing category). `PUT` replaces all fields.

**Paging**: `page` ≥ 1 (default 1), `pageSize` 1–100 (default 20). Response:
`{ "items": [...], "page": 1, "pageSize": 20, "totalCount": 24, "totalPages": 2 }`.

**Errors**: every error is `application/problem+json` with a stable `errorCode`:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict",
  "status": 409,
  "detail": "Cannot remove 10 unit(s) from product 100024: only 2 available.",
  "instance": "POST /api/products/100024/decrement-stock/10",
  "traceId": "00-7c33938af2cfed516925a89a26f4b8bb-605e972282405c51-00",
  "errorCode": "product.insufficient_stock",
  "requested": 10,
  "available": 2
}
```

| `errorCode`                          | Status | Meaning                                                    |
| ------------------------------------ | ------ | ---------------------------------------------------------- |
| `validation_failed`                  | 400    | See `errors` (field → messages)                            |
| `product.not_found`                  | 404    |                                                            |
| `product.insufficient_stock`         | 409    | Decrement larger than available stock (`available` included) |
| `product.stock_limit_exceeded`       | 409    | Increment would exceed 1,000,000                           |
| `product.concurrent_modification`    | 409    | Changed between read and write (no `If-Match` sent)        |
| `product.version_mismatch`           | 412    | `If-Match` no longer matches. Reload and retry.            |
| `internal_error`                     | 500    | Unexpected; details are only in the logs (correlate with `traceId`) |

---

## Key decisions

Each is detailed in an ADR under [`docs/adr`](docs/adr/README.md).

1. **Clean architecture, CQRS-lite.** Reads are no-tracking projections, writes go through tracked aggregates.
   No MediatR, no generic repository. ([0001](docs/adr/0001-clean-architecture.md))
2. **Ids from a bounded PostgreSQL sequence.** `nextval()` is atomic across connections, processes and transactions, so
   any number of instances can create products concurrently. ([0002](docs/adr/0002-product-id-generation.md))
3. **Stock changes are one conditional UPDATE.** The domain owns the rule (as an expression) and the database evaluates
   it atomically: no overselling, no lost updates, no retries. ([0003](docs/adr/0003-atomic-stock-updates.md))
4. **Optimistic concurrency** with PostgreSQL `xmin` exposed as `ETag`/`If-Match`. Prevents lost updates on `PUT`,
   including an editor overwriting stock that was sold meanwhile. ([0004](docs/adr/0004-optimistic-concurrency.md))
5. **Result pattern + RFC 9457 problem details** with stable error codes. Exceptions only for the unexpected.
   ([0005](docs/adr/0005-error-handling.md))
6. **PostgreSQL**, tested against the real engine. ([0006](docs/adr/0006-postgresql.md))
7. **Signals + `rxResource`** in the frontend, with immediate updates and a background refresh.
   ([0007](docs/adr/0007-frontend-state.md))

---

## Assumptions

* **Single currency, two decimals.** Prices are stored as `numeric(9,2)`, between 0.01 and 1,000,000. The UI shows EUR.
* **Stock is a whole number between 0 and 1,000,000.** The upper bound guards against data-entry mistakes and makes
  overflow impossible. Requested quantities for stock operations must be 1–1,000,000.
* **"Decrement more than available" is rejected (409)**, not clamped to zero. Partial fulfilment would hide the
  shortage from the caller.
* **"Return an OK response"** for stock endpoints means **200 with the updated product**. Clients get the new stock
  without a second call.
* **Search** is case-insensitive "contains" on the name only. A blank `name` is a 400 (use `GET /products` to list all).
  `%`, `_` and `\` typed by the user match literally.
* **Stock-level** bounds are inclusive and each is optional. Results are ordered by stock (lowest first), the most
  useful order for finding what needs restocking.
* **All list endpoints are paged** (default 20, max 100). Unbounded lists don't scale, and the response shape is the
  same everywhere.
* **Categories** are reference data, seeded by the migration and read-only through the API. They give the product a
  relationship (as the PDF suggests) without widening the scope with category CRUD.
* **Deleting is a hard delete.** Ids are never reused (sequence `NO CYCLE`), so a deleted product's id can't come back.
* **Authentication/authorization are out of scope** (not requested). See next steps.

---

## Trade-offs and deviations from common practice

| Choice                                                         | Why                                                                                     |
| -------------------------------------------------------------- | --------------------------------------------------------------------------------------- |
| Stock changes bypass load-modify-save (no `product.DecreaseStock()`) | Atomicity under concurrency. The rule stays in the domain as an expression evaluated by the database ([0003](docs/adr/0003-atomic-stock-updates.md)). |
| Nullable value types in request DTOs (`decimal? Price`)        | A missing field becomes a clear validation error instead of a silent `0` ([0005](docs/adr/0005-error-handling.md)). |
| No `EnableRetryOnFailure`                                      | The stock endpoints are not idempotent, and a blind retry after an ambiguous commit could apply a change twice. |
| `If-Match` optional (not 428 when absent)                      | Keeps the API easy to try. The UI always sends it, and stricter enforcement is a one-line change. |
| The write side re-reads through the query side after saving    | One mapping (the projection) for every response, with the DB-generated `version`. Costs one primary-key lookup. |
| FluentValidation instead of DataAnnotations                    | Rules are testable and shared by create and update, with no duplicate validation path in MVC. |
| Request/response contracts live in Application                 | Avoids duplicate DTOs and mapping for a 1:1 HTTP contract. Can be split if they diverge.        |
| Polling instead of push in the UI                              | Works across many API instances with no extra infrastructure. Up to 15 s of staleness ([0007](docs/adr/0007-frontend-state.md)). |
| Real PostgreSQL in tests (Docker required)                     | EF InMemory/SQLite would fake exactly the concurrency behaviour under test.                    |
| PostgreSQL rather than SQL Server                              | Runs natively everywhere, and provides `xmin`, bounded sequences and trigram search ([0006](docs/adr/0006-postgresql.md)). |

---

## Production readiness and next steps

Already in place: structured JSON logs in production, health checks (liveness/readiness), non-root container images,
problem details with trace ids, optimistic concurrency, database constraints, CI, pinned SDK/Node/package versions,
warnings as errors.

What a real deployment would add next:

* **Migrations as a deployment step.** Run a migration bundle (`dotnet ef migrations bundle`) or a reviewed SQL script
  in the pipeline, instead of at startup.
* **Secrets.** Take the connection string from a secret store (Key Vault / environment). The credentials in the repo
  are for local development only.
* **AuthN/AuthZ.** JWT bearer (e.g. Entra ID) with policies for write operations.
* **Idempotency keys** for the stock endpoints, so clients and the data layer can retry safely.
* **Observability.** OpenTelemetry traces and metrics (ASP.NET Core, EF Core, Npgsql) exported to the platform's backend.
* **Rate limiting** (`Microsoft.AspNetCore.RateLimiting`) and request size limits tuned per endpoint.
* **Server-side sorting** for the list, and **push updates** (SignalR) if near-real-time UI updates are required.
* **Non-sequential ids** (format-preserving permutation of the sequence value) if id enumeration becomes a concern.
* **End-to-end UI tests** (Playwright) for the main user journeys.

---

## Repository layout

```
.
├── backend/
│   ├── ProductManagement.sln          # open in Visual Studio / Rider
│   ├── global.json                    # pins the .NET SDK (and the MTP test runner)
│   ├── Directory.Build.props          # shared build settings, analyzers, warnings as errors
│   ├── Directory.Packages.props       # central NuGet versions
│   ├── dotnet-tools.json              # dotnet-ef
│   ├── src/
│   │   ├── ProductManagement.Domain/
│   │   ├── ProductManagement.Application/
│   │   ├── ProductManagement.Infrastructure/   # incl. Persistence/Migrations
│   │   └── ProductManagement.Api/              # incl. Dockerfile, .http requests
│   └── tests/
│       ├── ProductManagement.Domain.UnitTests/
│       ├── ProductManagement.Application.UnitTests/
│       ├── ProductManagement.Api.IntegrationTests/
│       ├── ProductManagement.Api.AcceptanceTests/   # Reqnroll features
│       └── ProductManagement.Testing/               # shared test infrastructure
├── frontend/                          # Angular 22 workspace (Dockerfile, nginx.conf, proxy.conf.json)
├── docs/
│   ├── adr/                           # architecture decision records
│   ├── scaffolding.md                 # how the projects were generated and what changed afterwards
│   └── images/
├── docker-compose.yml
└── .github/workflows/ci.yml
```

### Tech stack

| Area      | Stack                                                                                                   |
| --------- | ------------------------------------------------------------------------------------------------------- |
| Backend   | .NET 10, ASP.NET Core (controllers), EF Core 10, Npgsql, FluentValidation, OpenAPI + Swagger UI          |
| Database  | PostgreSQL 18                                                                                            |
| Frontend  | Angular 22 (standalone, zoneless, signals), Angular Material 22, RxJS, Vitest, ESLint, Prettier         |
| Testing   | xUnit v3 (Microsoft Testing Platform), Shouldly, NSubstitute, Testcontainers, Respawn, Reqnroll         |
| Delivery  | Docker, Docker Compose, nginx (unprivileged), GitHub Actions                                            |
