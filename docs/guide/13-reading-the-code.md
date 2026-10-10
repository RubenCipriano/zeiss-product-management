# 13. Reading the code

**After this chapter you will know** where to open the code for any question, how one click travels through every
file it touches, and what each C#, Angular and nginx construct in this code means in plain words. It is written
for someone who did not write the code, including someone who does not use .NET or Angular every day.

---

## The shape of the code

```
backend/
  ProductManagement.Core/            what the system does: products, stock, rules, use cases   (no database code)
    Products/  Categories/  Common/      one folder per feature; Common = errors, paging, results
  ProductManagement.Infrastructure/  how it is stored: PostgreSQL (EF Core), Redis, caching, migrations
  ProductManagement.Api/             how it is reached: HTTP controllers, security, rate limits, idempotency
  ProductManagement.UnitTests/       Core/ · Infrastructure/ · Api/  (fast, no database)
  ProductManagement.Api.IntegrationTests/  real HTTP against real PostgreSQL and Redis (Testcontainers)
  ProductManagement.Api.AcceptanceTests/   the user outcomes, in Gherkin (Given / When / Then)
  ci/                                pipeline helpers: migrate.sh (migrations in the cluster), release.sh
frontend/
  src/app/core/          once per app: sign-in, HTTP interceptors, browser cache, error reporting
  src/app/features/products/
    data-access/         talks to the API and holds the page's data (ProductsApi, ProductsStore)
    feature/             the page and the dialogs: what happens when the user does something (ProductActions)
    ui/                  pure display pieces: table, filters, summary cards, stock badge
  src/app/shared/        reusable widgets, form validators, pipes
  gateway/               the nginx in front of everything: routing, token check, rate limits, 1 s cache
  e2e/                   Playwright tests against a running system
deploy/
  apps/                  how the API, the web app and the gateway run: their Kubernetes manifests
  environments/          local, staging, production: settings and image tags per environment
  infra/                 the shared infrastructure (namespace infra): PostgreSQL, Redis, Loki + Grafana, Alloy
  scripts/               deploy.sh, infra.sh, create-database.sh, local-secrets.sh, validate.sh, smoke.sh
tools/                   local only: load-tests/ (k6 scenarios run inside the cluster), check-requirements.py
```

One rule holds the backend together: **Api → Infrastructure → Core**, never the other way. Core has no reference to
the database library, so business rules cannot accidentally depend on it; the compiler refuses. The frontend follows
the same idea: `ui/` components receive data and emit events, they never call the API.

## "I want to understand…" → open this

| Question | Backend | Frontend / gateway |
|---|---|---|
| What are the endpoints? | [`ProductsController.cs`](../../backend/ProductManagement.Api/Controllers/ProductsController.cs) | [`products-api.ts`](../../frontend/src/app/features/products/data-access/products-api.ts) |
| What are the rules of a product? | [`Product.cs`](../../backend/ProductManagement.Core/Products/Product.cs), [`ProductRules.cs`](../../backend/ProductManagement.Core/Products/ProductRules.cs) | [`validators.ts`](../../frontend/src/app/shared/forms/validators.ts) |
| How is input validated? | [`ProductWriteRequestValidator.cs`](../../backend/ProductManagement.Core/Products/Validation/ProductWriteRequestValidator.cs) | the form in [`product-form-dialog`](../../frontend/src/app/features/products/feature/product-form-dialog) |
| How does stock change without overselling? | [`StockCommandService.cs`](../../backend/ProductManagement.Core/Products/StockCommandService.cs) → [`ProductRepository.cs`](../../backend/ProductManagement.Infrastructure/Products/ProductRepository.cs) (one SQL statement) | [`product-actions.ts`](../../frontend/src/app/features/products/feature/product-actions.ts) |
| How are 6-digit ids made with many servers? | [`PostgresProductIdGenerator.cs`](../../backend/ProductManagement.Infrastructure/Products/PostgresProductIdGenerator.cs) (a database sequence) | — |
| How do errors become HTTP responses? | [`Result.cs`](../../backend/ProductManagement.Core/Common/Results/Result.cs), [`ErrorType.cs`](../../backend/ProductManagement.Core/Common/Errors/ErrorType.cs), [`ApiControllerBase.cs`](../../backend/ProductManagement.Api/Http/ApiControllerBase.cs) | [`api-error-interceptor.ts`](../../frontend/src/app/core/http/api-error-interceptor.ts) |
| Where is the cache? | [`CachingProductQueries.cs`](../../backend/ProductManagement.Infrastructure/Caching/CachingProductQueries.cs) (Redis) | [`client-cache-interceptor.ts`](../../frontend/src/app/core/http/client-cache-interceptor.ts) (browser), [`default.conf.template`](../../frontend/gateway/default.conf.template) (gateway, 1 s) |
| How is a retried request applied once? | [`IdempotencyFilter.cs`](../../backend/ProductManagement.Api/Idempotency/IdempotencyFilter.cs) | `crypto.randomUUID()` in [`product-actions.ts`](../../frontend/src/app/features/products/feature/product-actions.ts) |
| Who may do what? | [`Policies`](../../backend/ProductManagement.Api/Security) and the `[Authorize]` lines in the controller | [`auth-guard.ts`](../../frontend/src/app/core/auth/auth-guard.ts), [`jwt.js`](../../frontend/gateway/jwt.js) |
| What is wired to what at startup? | [`Program.cs`](../../backend/ProductManagement.Api/Program.cs), [`CoreServiceCollectionExtensions.cs`](../../backend/ProductManagement.Core/CoreServiceCollectionExtensions.cs), [`InfrastructureServiceCollectionExtensions.cs`](../../backend/ProductManagement.Infrastructure/InfrastructureServiceCollectionExtensions.cs) | [`app.config.ts`](../../frontend/src/app/app.config.ts), [`app.routes.ts`](../../frontend/src/app/app.routes.ts) |
| How does the page stay up to date? | — | [`products-store.ts`](../../frontend/src/app/features/products/data-access/products-store.ts) |
| How is the database shaped? | [`ProductConfiguration.cs`](../../backend/ProductManagement.Infrastructure/Persistence/Configurations/ProductConfiguration.cs), [`Migrations/`](../../backend/ProductManagement.Infrastructure/Persistence/Migrations) | — |

## One click, file by file

The user clicks **−10** on a product row. Everything below happens in order; each step names the file to open.

**In the browser**

1. [`product-table.ts`](../../frontend/src/app/features/products/ui/product-table/product-table.ts): the table only
   *reports* the click as an event, `quickChange` (product, `remove`, 10). It does not know about the API.
2. [`products-page.html`](../../frontend/src/app/features/products/feature/products-page/products-page.html) passes
   the event to [`ProductActions.quickChange`](../../frontend/src/app/features/products/feature/product-actions.ts),
   which creates one **idempotency key** for this click (`crypto.randomUUID()`), so a retry can never remove 20.
3. [`ProductsStore.adjustStock`](../../frontend/src/app/features/products/data-access/products-store.ts) calls
   [`ProductsApi.adjustStock`](../../frontend/src/app/features/products/data-access/products-api.ts), which sends
   `POST /api/products/{id}/decrement-stock/10` with the `Idempotency-Key` header.
4. The request passes through the **interceptors** listed in
   [`app.config.ts`](../../frontend/src/app/app.config.ts): the browser cache is cleared for writes
   ([`client-cache-interceptor.ts`](../../frontend/src/app/core/http/client-cache-interceptor.ts)), the access token is
   added ([`auth-interceptor.ts`](../../frontend/src/app/core/auth/auth-interceptor.ts)), and a failure will be turned
   into a readable `ApiError` ([`api-error-interceptor.ts`](../../frontend/src/app/core/http/api-error-interceptor.ts)).

**In the gateway** ([`default.conf.template`](../../frontend/gateway/default.conf.template), `location ^~ /api/`)

5. Rate limit per client address (too many requests → 429).
6. `auth_request /_jwt`: [`jwt.js`](../../frontend/gateway/jwt.js) checks the token's signature and expiry; a bad
   token stops here with 401, without reaching the API.
7. [`trace.js`](../../frontend/gateway/trace.js) adds a `traceparent` header: the same trace id is written in the
   gateway's log line and in every API log line for this request.
8. The request goes to the least busy API replica (`least_conn`). Writes are never cached.

**In the API**

9. [`Program.cs`](../../backend/ProductManagement.Api/Program.cs): the request passes the middleware in order:
   forwarded headers, error handling, CORS, **authentication** (who is it?), **rate limiting** per user,
   **authorization** (may they?).
10. [`ProductsController.DecrementStock`](../../backend/ProductManagement.Api/Controllers/ProductsController.cs): its
    attributes say it needs the write permission, the stricter write rate limit and `[Idempotent]`.
11. [`IdempotencyFilter`](../../backend/ProductManagement.Api/Idempotency/IdempotencyFilter.cs) runs first: if this key
    was already answered, it replays that answer without running anything again.
12. [`StockCommandService`](../../backend/ProductManagement.Core/Products/StockCommandService.cs) (Core) checks the
    quantity and builds a [`StockAdjustment`](../../backend/ProductManagement.Core/Products/StockAdjustment.cs):
    "−10, but only if at least 10 are in stock".
13. [`ProductRepository.TryAdjustStockAsync`](../../backend/ProductManagement.Infrastructure/Products/ProductRepository.cs)
    (Infrastructure) runs **one SQL statement** that checks and changes the stock and writes the history row together.
    Two people taking the last units at the same moment cannot both succeed.
14. The service reads the product again (through the Redis cache,
    [`CachingProductQueries`](../../backend/ProductManagement.Infrastructure/Caching/CachingProductQueries.cs), whose
    lists were just invalidated) and returns it, or an `Error` such as "insufficient stock".
15. [`ApiControllerBase`](../../backend/ProductManagement.Api/Http/ApiControllerBase.cs) turns the result into HTTP:
    200 with the product, or 409 with a problem-details body and an `errorCode`.

**Back in the browser**

16. The store shows the new stock at once and reloads the list, bypassing the caches, so the user sees the result of
    their own change. Other users see it within 15 s (automatic refresh) or on their next page change.

The same path, drawn: [flows.md](../flows.md) and [chapter 2](02-request-journey.md).

## Glossary: C# and ASP.NET Core

| Term | In plain words | Where to see it |
|---|---|---|
| `sealed` (not used) | Would forbid inheriting from a class. The code does not use it: classes are left open, as in most C# codebases, and behaviour is extended by **wrapping** (decorators) rather than inheriting. You may still meet it in .NET's own types. | — |
| `internal` | Visible only inside its own project. Implementations are internal; other projects only see the interface. Unit tests are allowed in with `InternalsVisibleTo`. | [`ProductQueryService.cs`](../../backend/ProductManagement.Core/Products/ProductQueryService.cs) |
| `record` | A type that carries data: two records with the same values are equal, and it prints readably. Used for request and response shapes. | [`ProductResponse.cs`](../../backend/ProductManagement.Core/Products/Contracts/ProductResponse.cs) |
| Primary constructor | `class X(IFoo foo)`: the dependencies are declared on the class line itself; no constructor body, no field assignments. | [`ProductCommandService.cs`](../../backend/ProductManagement.Core/Products/ProductCommandService.cs) |
| `partial` + `[LoggerMessage]` | The compiler writes the logging code at build time: faster than `logger.LogInformation(...)`, and the message's fields are checked when compiling. `partial` lets that generated half live in another file. | the end of [`StockCommandService.cs`](../../backend/ProductManagement.Core/Products/StockCommandService.cs) |
| `Result<T>` / `Error` | Expected failures (not found, invalid input, not enough stock) are **returned**, not thrown. The method's signature shows it can fail, and one place maps `Error.Type` to an HTTP status. Exceptions are for bugs and outages only (500). | [`Result.cs`](../../backend/ProductManagement.Core/Common/Results/Result.cs), [`ErrorType.cs`](../../backend/ProductManagement.Core/Common/Errors/ErrorType.cs) |
| Dependency injection, `AddCore()` / `AddInfrastructure()` | Classes ask for interfaces in their constructor; the framework supplies the implementation. Each project registers its own classes in one `AddXxx` method, so `Program.cs` reads as a list of parts. *Scoped* = one per request, *Singleton* = one per app. | [`Program.cs`](../../backend/ProductManagement.Api/Program.cs) |
| Decorator | A class with the same interface as another that wraps it and adds one behaviour; callers cannot tell. The Redis cache is added to the queries this way, without touching Core. | [`CachingProductQueries.cs`](../../backend/ProductManagement.Infrastructure/Caching/CachingProductQueries.cs) |
| Attributes `[HttpGet]`, `[Authorize]`, `[Idempotent]` | Labels the framework reads: the URL, the HTTP method, the permission, the rate limit, which filter to run, without code in the method body. | [`ProductsController.cs`](../../backend/ProductManagement.Api/Controllers/ProductsController.cs) |
| `async` / `await`, `CancellationToken` | Waiting for the database or the network without blocking a thread, so one server serves many requests. The token fires when the client gives up, and abandoned work stops. | every controller action |
| Middleware and filters | Middleware: the `app.Use…` steps every request passes through, in order. Filters: code around controller actions only (the idempotency check is one). | [`Program.cs`](../../backend/ProductManagement.Api/Program.cs), [`IdempotencyFilter.cs`](../../backend/ProductManagement.Api/Idempotency/IdempotencyFilter.cs) |
| `IOptions<T>` | A configuration section bound to a typed class and **validated at startup**: a wrong setting stops the app at boot instead of at the first request. | [`CacheOptions.cs`](../../backend/ProductManagement.Infrastructure/Caching/CacheOptions.cs) |
| EF Core, migrations | The database library: C# classes mapped to tables; migrations are generated, versioned C# files that change the schema step by step. | [`ProductConfiguration.cs`](../../backend/ProductManagement.Infrastructure/Persistence/Configurations/ProductConfiguration.cs), [`Migrations/`](../../backend/ProductManagement.Infrastructure/Persistence/Migrations) |
| FluentValidation | Input rules written as `RuleFor(x => x.Price).GreaterThan(0)`; every broken rule comes back in one 400 with a message per field. | [`ProductWriteRequestValidator.cs`](../../backend/ProductManagement.Core/Products/Validation/ProductWriteRequestValidator.cs) |
| Problem details | The standard JSON shape for HTTP errors (`title`, `status`, `detail`), plus this API's stable `errorCode`. | [`ApiControllerBase.cs`](../../backend/ProductManagement.Api/Http/ApiControllerBase.cs) |
| `TimeProvider` | An injectable clock instead of `DateTime.Now`, so tests can control time. | [`CoreServiceCollectionExtensions.cs`](../../backend/ProductManagement.Core/CoreServiceCollectionExtensions.cs) |
| Liveness / readiness | `/health/live`: is the process up? `/health/ready`: can it reach the database, so may it receive traffic? | [`DatabaseHealthCheck.cs`](../../backend/ProductManagement.Infrastructure/Persistence/DatabaseHealthCheck.cs) |

## Glossary: Angular and the browser

| Term | In plain words | Where to see it |
|---|---|---|
| Standalone component | A component that lists what its template uses in its own `imports`, without NgModules. | [`app.ts`](../../frontend/src/app/app.ts) |
| `signal`, `computed` | A signal holds a value; anything that reads it (the template, a `computed`) updates by itself when it changes. A `computed` is a value derived from signals. | [`auth.ts`](../../frontend/src/app/core/auth/auth.ts) |
| `effect` | Runs a function again whenever a signal it read changes; used for side effects such as prefetching the next page. | [`products-store.ts`](../../frontend/src/app/features/products/data-access/products-store.ts) |
| `input()` / `output()` | What a component receives from its parent, and the events it sends back up. | [`product-table.ts`](../../frontend/src/app/features/products/ui/product-table/product-table.ts) |
| `inject()` | Asks Angular for a service instance (dependency injection, as in the backend). | most files |
| `rxResource` | A loader driven by signals: when its parameters change it cancels the request in flight and starts a new one, and exposes `value`, `isLoading` and `error` as signals. | [`products-store.ts`](../../frontend/src/app/features/products/data-access/products-store.ts) |
| HTTP interceptor | A function that runs on every HTTP request and response: adds the token, serves the cache, turns errors into `ApiError`. | [`app.config.ts`](../../frontend/src/app/app.config.ts) |
| Stale-while-revalidate | Show the cached copy at once, then replace it with the fresh answer. | [`client-cache-interceptor.ts`](../../frontend/src/app/core/http/client-cache-interceptor.ts) |
| Route guard | A function the router runs before opening a page: allow, or redirect to sign-in. | [`auth-guard.ts`](../../frontend/src/app/core/auth/auth-guard.ts) |
| Lazy loading | `loadComponent: () => import(...)`: the page's code is a separate file, downloaded on first visit. | [`app.routes.ts`](../../frontend/src/app/app.routes.ts) |
| Pipe | A formatting function used in templates with `\|`, e.g. `+10` / `−10`. | [`signed-number-pipe.ts`](../../frontend/src/app/shared/pipes/signed-number-pipe.ts) |
| `ErrorHandler` | Angular's single place for errors; replaced here to also report them to Grafana. | [`reporting-error-handler.ts`](../../frontend/src/app/core/observability/reporting-error-handler.ts) |
| Zoneless + `OnPush` | The screen updates because a signal changed, not after every browser event: less work, predictable updates. | [ADR 0007](../adr/0007-frontend-state.md) |
| Tailwind | Small single-purpose CSS classes in templates (`px-4`, `text-sm`) instead of separate stylesheets. | any `.html` file |
| Angular CDK | Unstyled, accessible building blocks from the Angular team: dialogs (focus trap, Escape to close), menus. | [`app-dialog.ts`](../../frontend/src/app/shared/ui/dialog/app-dialog.ts) |

## Glossary: HTTP and the gateway

| Term | In plain words | Where to see it |
|---|---|---|
| ETag / `If-Match` | The product's version number. Edits send it back; if someone else changed the product meanwhile, the answer is 412 instead of silently overwriting their change. | [`products-api.ts`](../../frontend/src/app/features/products/data-access/products-api.ts), [`EntityTag.cs`](../../backend/ProductManagement.Api/Http/EntityTag.cs) |
| `Idempotency-Key` | One random key per user action: if the same request arrives twice (double click, retry), it is applied once. | [`IdempotencyFilter.cs`](../../backend/ProductManagement.Api/Idempotency/IdempotencyFilter.cs) |
| JWT, ES256 | The access token: a signed note saying who you are and your roles. The API signs it with a private key; the gateway and the API check it with the public key. | [`jwt.js`](../../frontend/gateway/jwt.js) |
| njs, `auth_request` | nginx's small JavaScript engine, and the directive that asks it "is this token valid?" before forwarding a request. | [`default.conf.template`](../../frontend/gateway/default.conf.template) |
| Microcache | The gateway keeps each read answer for 1 s: a thousand users opening the same page cost the API one request. | [`default.conf.template`](../../frontend/gateway/default.conf.template) |
| `least_conn` | Load balancing that sends each request to the API replica with the fewest requests in progress. | [`default.conf.template`](../../frontend/gateway/default.conf.template) |
| CSP | A header listing where the page may load code and data from (here: only itself), which blocks injected scripts. | [`security-headers.conf`](../../frontend/gateway/security-headers.conf) |
| `traceparent` | A standard header carrying the request's trace id, so the gateway's and the API's log lines can be matched in Grafana. | [`trace.js`](../../frontend/gateway/trace.js) |

## Conventions you will notice

* **Each file starts with what it is and its role**, then why it is built that way (briefly, with a link to the ADR
  or guide chapter that has the detail). Comments explain *why*, not what the next line does.
* **Names read as English**: `StockCommandService.DecrementStockAsync`, `ProductErrors.InsufficientStock`,
  `ProductsStore.reload({ fresh: true })`. Async methods end in `Async` (backend).
* **Failures are values** in the backend (`Result`/`Error`), and arrive as `ApiError` in the frontend; both carry the
  same `errorCode`.
* **Numbers live in one place**: limits in [`ProductRules.cs`](../../backend/ProductManagement.Core/Products/ProductRules.cs)
  (mirrored by database check constraints), timings and sizes as named constants; measurements in the ADRs, not in
  code comments.
* **Tests mirror the code**: `UnitTests/Core/Products/StockCommandServiceTests.cs` tests
  `Core/Products/StockCommandService.cs`; test names are sentences
  (`DecrementStock_BeyondAvailableStock_IsRejectedAndLeavesStockUnchanged`). See [chapter 10](10-testing-and-quality.md).
