# Product Management — API

The backend of the Product Management system: a REST API built with .NET 10 / ASP.NET Core, EF Core and PostgreSQL,
with Redis as a shared cache and coordination store. It manages a catalogue of products and their stock, issues its own
access tokens to registered clients, and enforces three roles. This repository holds the code, the tests, the
container image and the CI pipeline. The web app and the gateway are in [frontend](https://gitlab.com/zeiss/frontend);
the Kubernetes manifests, the environments and the deployment scripts are in [deploy](https://gitlab.com/zeiss/deploy).

This README is the API's specification (every endpoint, header, error code and setting) and explains how the code is
built, why, and how to run, test and release it. A release is a version tag (`v1.4.0`): the pipeline publishes the
image `registry.gitlab.com/zeiss/backend/api:1.4.0`, and the deploy repository decides when an environment runs it.

## Contents

- [What it does](#what-it-does)
- [API specification](#api-specification)
  - [Endpoints](#endpoints)
  - [Authentication and roles](#authentication-and-roles)
  - [Product and paging shapes](#product-and-paging-shapes)
  - [Headers: ETag, If-Match, If-None-Match, Idempotency-Key](#headers-etag-if-match-if-none-match-idempotency-key)
  - [Errors](#errors)
  - [Rate limits](#rate-limits)
  - [Health and documentation endpoints](#health-and-documentation-endpoints)
- [Architecture](#architecture)
- [Data model](#data-model)
- [Consistency](#consistency)
- [Caching](#caching)
- [Security](#security)
- [Configuration](#configuration)
- [The image and its commands](#the-image-and-its-commands)
  - [Outside Kubernetes: docker run, Docker Compose](#outside-kubernetes-docker-run-docker-compose)
- [Run locally](#run-locally)
- [Tests](#tests)
- [CI](#ci)
- [How to release a version](#how-to-release-a-version)
- [Design decisions](#design-decisions)
- [Troubleshooting](#troubleshooting)

## What it does

The assessment asked for a product API. Every requirement is met and tested over real HTTP against a real PostgreSQL:

| Requirement | How |
| --- | --- |
| CRUD: `GET/POST /api/products`, `GET/PUT/DELETE /api/products/{id}` | lists are paged (`page`, `pageSize` ≤ 100) and every item carries its stock |
| `POST /api/products/{id}/decrement-stock/{quantity}` and `…/add-to-stock/{quantity}` | `200 OK` with the updated product; never below 0 (`409`), quantity validated (`400`) |
| `GET /api/products/search?name=` | partial, case-insensitive match (trigram index; `%` and `_` are literal) |
| `GET /api/products/stock-level?min=&max=` | inclusive range, either bound optional |
| Unique 6-digit ids, also with several instances | a PostgreSQL sequence 100000–999999: every instance draws from the same counter; `503 product.ids_exhausted` when used up |
| Validation on create and update | FluentValidation, every invalid field named in one RFC 9457 problem-details response |
| EF Core code-first migrations and seeding | migrations in `ProductManagement.Infrastructure/Persistence/Migrations`; categories seeded by migration, a sample catalogue (24 products) on an empty database |
| Unit tests (BDD optional) | 385 tests: 216 unit, 153 integration (Testcontainers), 16 BDD scenarios (Reqnroll) |

Added because a real deployment needs them: sign-in with ES256 JWTs issued by the API to registered clients, and
three roles (viewer, stock clerk, manager); optimistic concurrency with `ETag` / `If-Match` (`412` instead of a lost
update); `Idempotency-Key`, so a retried request is applied once (Redis first, a unique key in the stock ledger as the
backstop); a stock ledger with each product's history; a Redis read cache; per-user rate limits shared by every
replica; health checks; JSON logs that carry the request's trace id.

## API specification

All endpoints are under the API's base address (locally http://localhost:5080, behind the gateway the application's
address). Bodies are JSON with camelCase names. Numbers must be JSON numbers (`"price": "12"` is refused). Every error
is a problem-details object (see [Errors](#errors)). `ProductManagement.Api/ProductManagement.Api.http` has a ready
request for every endpoint.

### Endpoints

| Method and path | Permission (roles) | Request | Success | Errors (status `errorCode`) |
| --- | --- | --- | --- | --- |
| `POST /api/auth/token` | none | body `{"clientId","clientSecret"}` | `200` `{"accessToken","tokenType":"Bearer","expiresIn":3600}` | `400 validation_failed`, `401 invalid_client`, `429 too_many_failed_attempts`, `429 rate_limited` |
| `GET /.well-known/jwks.json` | none | | `200` the public signing key (JWKS), `Cache-Control: public, max-age=300` | |
| `GET /api/categories` | read (all roles) | | `200` `[{"id","name"}]`, ordered by name | `401`, `403`, `429` |
| `GET /api/products` | read | `?page=&pageSize=` | `200` page of products, ordered by name, then id | `400 validation_failed` |
| `GET /api/products/search` | read | `?name=` (required, non-blank, ≤ 200), `page`, `pageSize` | `200` page of products whose name contains `name` (case-insensitive; `%`, `_`, `\` match literally), ordered by name, then id | `400 validation_failed` |
| `GET /api/products/stock-level` | read | `?min=&max=` (each optional, ≥ 0, `max ≥ min`), `page`, `pageSize` | `200` page of products with `min ≤ stock ≤ max`, ordered by stock, then name, then id | `400 validation_failed` |
| `GET /api/products/{id}` | read | optional `If-None-Match` | `200` product with `ETag`; `304` (no body) if `If-None-Match` names the current version | `404 product.not_found` |
| `GET /api/products/{id}/stock-movements` | read | `?page=&pageSize=` | `200` page of the product's stock history, newest first | `400 validation_failed`, `404 product.not_found` |
| `POST /api/products` | products:write (manager) | body: product fields; optional `Idempotency-Key` | `201` product, `Location: /api/products/{id}`, `ETag` | `400 validation_failed` (also an unknown `categoryId`), `503 product.ids_exhausted`, idempotency errors |
| `PUT /api/products/{id}` | products:write (manager) | body: every product field; optional `If-Match` | `200` product with the new `ETag` | `400 validation_failed`, `404 product.not_found`, `409 product.concurrent_modification`, `412 product.version_mismatch` |
| `DELETE /api/products/{id}` | products:write (manager) | optional `If-Match` | `204` | `400` (malformed `If-Match`), `404 product.not_found`, `409 product.concurrent_modification`, `412 product.version_mismatch` |
| `POST /api/products/{id}/decrement-stock/{quantity}` | stock:write (stock-clerk, manager) | no body; optional `Idempotency-Key` | `200` product with its new stock and `ETag` | `400 validation_failed` (quantity not an integer from 1 to 1,000,000), `404 product.not_found`, `409 product.insufficient_stock`, idempotency errors |
| `POST /api/products/{id}/add-to-stock/{quantity}` | stock:write (stock-clerk, manager) | the same | the same | the same, with `409 product.stock_limit_exceeded` instead of insufficient stock |

Every endpoint except the first two needs `Authorization: Bearer <access token>` (`401` without a valid one, `403`
when the role lacks the permission) and may answer `429 rate_limited`. `{id}` must be an integer, otherwise the route
does not match (`404 http_404`); `{quantity}` is matched as text, so a non-numeric quantity is `400`.

**Product fields** (create and update; update replaces every field, so a missing `description` clears it):

| Field | Rule |
| --- | --- |
| `name` | required, not blank, at most 200 characters |
| `description` | optional, at most 2,000 characters |
| `price` | required, 0.01 to 1,000,000, at most 2 decimal places |
| `stockQuantity` | required, 0 to 1,000,000 (on update, a change is recorded in the ledger as a `Correction`) |
| `categoryId` | required, an existing category (1 to 6 in the seeded data) |

The id is never sent: the server assigns a six-digit id.

### Authentication and roles

The API is its own token issuer, for registered **clients** (also called consumers): the web app's users, load tests,
other services. A client exchanges its id and secret for a short-lived **access token**, then sends it with every
request.

```bash
curl -s -X POST http://localhost:5080/api/auth/token -H 'Content-Type: application/json' \
  -d '{"clientId":"manager","clientSecret":"manager"}'
# {"accessToken":"eyJhbGciOiJFUzI1NiIs...","tokenType":"Bearer","expiresIn":3600}

curl -s http://localhost:5080/api/categories -H "Authorization: Bearer <accessToken>"
```

* **The token** is a JWT (a signed JSON document) signed with **ES256** (ECDSA on the P-256 curve). Header: `alg`
  `ES256`, `kid` (the key's RFC 7638 thumbprint), `typ` `JWT`. Claims: `iss` and `aud` (`product-management-api`),
  `sub` (the client id), `roles` (one value per role), `jti`, `iat`, `nbf`, `exp`. Lifetime:
  `Authentication:AccessTokenLifetime`, 1 hour by default.
* **Verification** accepts only ES256, checks issuer, audience, lifetime (30 s clock tolerance) and signature. The
  public key is published at `/.well-known/jwks.json`, so the gateway (and any other service) can verify tokens without
  being able to issue them.
* **Clients** are configuration (`Authentication:Consumers`, or the file `config/consumers.json`, which is how
  Kubernetes provides them): `ClientId`, `SecretHash` (PBKDF2, see [Security](#security)) and `Roles`. The API stores
  no secret, only its hash.
* **Sign-in failures** get one answer, `401 invalid_client`, whether the client id is unknown or the secret is wrong
  (an unknown id is checked against a dummy hash, so it takes as long). After 10 failures from one client IP within a
  minute (counted across all replicas in Redis), that IP gets `429 too_many_failed_attempts` with `Retry-After` until
  the minute is over. Independently, each replica allows 20 token requests per client IP per minute.

| Role | Permissions | May |
| --- | --- | --- |
| `viewer` | `products:read` | list, search, filter and read products, categories and stock history |
| `stock-clerk` | `products:read`, `stock:write` | the above, plus add and remove stock |
| `manager` | `products:read`, `stock:write`, `products:write` | the above, plus create, edit and delete products |

Endpoints require a *permission*, never a role; the mapping is in one place (`Security/Policies.cs`). Any endpoint
without an explicit policy still requires a signed-in user.

### Product and paging shapes

A product (every product endpoint returns this shape):

```json
{"id":147044,"name":"Atlas Acetate Frame Burgundy M-333","description":null,"price":166.04,"stockQuantity":0,
 "categoryId":2,"categoryName":"Frames","createdAt":"2025-03-24T19:26:04.284548+00:00",
 "updatedAt":"2026-08-08T01:30:43.309125+00:00","version":789}
```

`version` changes on every save (it is PostgreSQL's row version, `xmin`) and is also sent as the `ETag` header
(`"789"`).

A page (lists, search, stock level, history):

```json
{"items":[ ... ],"page":1,"pageSize":20,"totalCount":8354,"totalPages":418}
```

| Parameter | Default | Allowed |
| --- | --- | --- |
| `page` | 1 | 1 to 21,474,836 (1-based) |
| `pageSize` | 20 | 1 to 100 |

A stock movement (history entry): `{"id","quantityChange","stockAfter","reason","performedBy","occurredAt"}`, where
`quantityChange` is positive for additions, `reason` is `InitialStock`, `StockAdded`, `StockRemoved` or `Correction`,
and `performedBy` is the client id that made the change.

### Headers: ETag, If-Match, If-None-Match, Idempotency-Key

**`ETag` / `If-Match` (optimistic concurrency).** Every product response carries `ETag: "<version>"`. Send it back in
`If-Match` on `PUT` or `DELETE` to say "only if nobody changed it since I read it". If the product changed meanwhile,
the answer is `412 product.version_mismatch` and nothing is written: two people editing at once never silently
overwrite each other. `If-Match` is optional (absent or `*` = no precondition); it must be one strong tag (`"123"`),
otherwise `400`. Without `If-Match`, a write that collides with another write at the same moment gets
`409 product.concurrent_modification`.

**`If-None-Match` (conditional read).** `GET /api/products/{id}` with `If-None-Match: "<version>"` answers `304 Not
Modified` without a body while the product is unchanged.

**`Idempotency-Key` (safe retries).** On `POST /api/products`, `decrement-stock` and `add-to-stock` a client may send
`Idempotency-Key: <its own id for this action>` (1 to 128 letters, digits, `-`, `_`, `.`, `:`; a UUID per user action
is typical). The same request sent twice with the same key is applied once:

| Situation | Answer |
| --- | --- |
| first request with this key | processed normally; the response (any status below 500) is stored for `Idempotency:KeyLifetime` (24 h by default, 1 h as deployed) |
| same key, same request, after it completed (on any replica) | the stored status and body, with `ETag`, `Location` and `Idempotent-Replayed: true`; nothing runs again |
| same key while the first request is still running | `409 idempotency_key_in_progress`, `Retry-After: 1` |
| same key, different request (method, path, query or body differ) | `422 idempotency_key_reused` |
| malformed key | `400 idempotency_key_invalid` |
| the key store (Redis) is unreachable | `503 idempotency_unavailable` (exactly-once cannot be guaranteed; retry later with the same key) |
| the first request failed with 5xx | nothing is stored: retry with the same key |

Keys are scoped to the caller (`sub`), so clients can never see or block each other's keys. Behind the store there is a
database backstop: the key is written with the stock-ledger entry under a unique index, so even after the store has
forgotten a key, a repeated stock change or create is never applied twice (a repeated create returns the product it
created; a key reused for a different change gets `422`). Without the header, requests behave as usual.

### Errors

Every error is an RFC 9457 **problem details** object (`application/problem+json`) with a stable, machine-readable
`errorCode`. Clients branch on `errorCode`, never on the text. Examples, as the API returns them:

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,
 "detail":"Cannot remove 1000000 unit(s) from product 147044: only 0 available.",
 "instance":"POST /api/products/147044/decrement-stock/1000000",
 "traceId":"00-2d42cb3d27d450e74d3e8dee6f4a0641-932f3f2dd06a6354-01",
 "errorCode":"product.insufficient_stock","requested":1000000,"available":0}
```

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.",
 "status":400,"detail":"One or more validation errors occurred.","instance":"POST /api/products",
 "errors":{"name":["'Name' must not be empty."],
           "price":["'Price' must be between 0.01 and 1000000. You entered 0.001.","'Price' must not have more than 2 decimal places."],
           "stockQuantity":["'Stock Quantity' must not be empty."]},
 "traceId":"00-516f6cdf63a55f9a95344859e66aeca8-6efa835ccf3e4789-01","errorCode":"validation_failed"}
```

`traceId` is the request's W3C trace context: the same id is on every log line of the request, in the API and in the
gateway. `errors` keys are the field names as the client sent them.

| Status | `errorCode` | When | Extra members |
| --- | --- | --- | --- |
| 400 | `validation_failed` | invalid body, query, route value or `If-Match`; unknown `categoryId` | `errors` (field → messages) |
| 400 | `idempotency_key_invalid` | malformed `Idempotency-Key` | |
| 401 | `invalid_client` | wrong client id or secret at the token endpoint | |
| 401 | `http_401` | missing or invalid access token (from the API) | |
| 401 | `invalid_token` | the same, rejected by the gateway before the API | |
| 403 | `http_403` | the token's roles lack the endpoint's permission | |
| 404 | `product.not_found` | no product with that id | |
| 404 | `http_404` | unknown route | |
| 405, 415, ... | `http_<status>` | other framework errors (wrong method, wrong content type) | |
| 409 | `product.insufficient_stock` | removing more than is in stock | `requested`, `available` |
| 409 | `product.stock_limit_exceeded` | adding beyond 1,000,000 | `requested`, `available`, `maximum` |
| 409 | `product.concurrent_modification` | another request changed the product during this one (no `If-Match` sent) | |
| 409 | `idempotency_key_in_progress` | the same key is still being processed | header `Retry-After` |
| 412 | `product.version_mismatch` | `If-Match` names an old version | |
| 422 | `idempotency_key_reused` | the key was used for a different request | |
| 429 | `rate_limited` | over a rate limit | header `Retry-After` |
| 429 | `too_many_failed_attempts` | too many failed sign-ins from this IP | header `Retry-After` |
| 500 | `internal_error` | a bug or an outage; details only in the logs | |
| 503 | `product.ids_exhausted` | all 900,000 six-digit ids are used | |
| 503 | `idempotency_unavailable` | Redis unreachable for a request with `Idempotency-Key` | |

Expected failures are returned by the use cases as values, not thrown; one place (`Http/ApiControllerBase.cs`) maps
the kind of error to the status. Unexpected exceptions go to `ErrorHandling/GlobalExceptionHandler.cs`, which logs them
and answers 500 without internal details (a request aborted by the client is logged at debug level with status 499).

### Rate limits

Token buckets: a caller may send up to *burst* requests at once, then on average *rate* per second. Over the limit:
`429 rate_limited` with `Retry-After`.

| Limit | Applies to | Partitioned by | Default (and as deployed) | Shared across replicas |
| --- | --- | --- | --- | --- |
| all requests | every endpoint except `/health/*` | signed-in user (`sub`), else client IP | 50 per second, burst 100 | yes (Redis; per replica if Redis is down) |
| writes | create, update, delete, stock changes | the same | 10 per second, burst 20 | yes |
| token endpoint | `POST /api/auth/token` | client IP | 20 per minute (fixed window) | no, per replica |
| failed sign-ins | `POST /api/auth/token` | client IP | 10 failures per minute, then blocked for the rest of the minute | yes (Redis) |

The client IP is the address the gateway forwards in `X-Forwarded-For`. In front of all this, the gateway limits each
client IP to 200 requests per second (burst 400).

### Health and documentation endpoints

| Path | Auth | Meaning |
| --- | --- | --- |
| `GET /health/live` | none | the process is up (no dependency checked): `200 Healthy` |
| `GET /health/ready` | none | may this replica get traffic? Runs `SELECT 1` on a dedicated one-connection pool (2 s budget) and pings Redis. Database down: `503 Unhealthy`. Redis down: `200 Degraded` (the API still works, slower), so a cache outage never takes replicas out of service |
| `GET /swagger`, `GET /openapi/v1.json` | none | Swagger UI and the OpenAPI document, only when `OpenApi:Enabled` is `true` (Development, local cluster, staging) |

The gateway does not route `/health/*`, `/swagger` or `/openapi` from outside; reach them on the API directly
(`kubectl -n product-management port-forward deployment/api 5080:8080`).

## Architecture

Three projects, one dependency rule:

```
ProductManagement.Api            how it is reached: HTTP, security, rate limits, idempotency, OpenAPI, health, commands
   │  references
   ▼
ProductManagement.Infrastructure how it is stored: EF Core + PostgreSQL, Redis (cache, idempotency, limits), migrations
   │  references
   ▼
ProductManagement.Core           what the system does: entities, rules, validation, use cases, interfaces
                                 (no EF Core, no ASP.NET Core: the compiler enforces it)
```

| Project | Folders |
| --- | --- |
| `ProductManagement.Core` | `Products/` (the `Product` and `StockMovement` entities, `ProductRules`, use cases `ProductQueryService`, `ProductCommandService`, `StockCommandService`, `StockAdjustment`, `ProductErrors`, `Contracts/`, `Validation/`, `Abstractions/` = interfaces Infrastructure implements), `Categories/`, `Common/` (`Result`, `Error`, paging, persistence and request abstractions) |
| `ProductManagement.Infrastructure` | `Persistence/` (DbContext, configurations, migrations, unit of work, seeding, database health check), `Products/` (repository, queries, id generator), `Categories/`, `Caching/` (decorators, Redis cache, single-flight), `Idempotency/`, `Security/` (Redis rate limiter, failed-sign-in throttle), `Redis/`, `Edge/` (interfaces the Api project uses) |
| `ProductManagement.Api` | `Controllers/` (`AuthController`, `ProductsController`, `CategoriesController`), `Http/` (result → HTTP mapping, problem details, ETag), `Security/` (token issuing and checking, policies, secret hashing), `RateLimiting/`, `Idempotency/`, `ErrorHandling/`, `Observability/`, `Configuration/`, `Hosting/` (the `migrate`, `hash-secret`, `generate-signing-key` commands), `Program.cs` |

**A request's path through the code** (`POST /api/products/100042/decrement-stock/5`):

1. **Middleware** (`Program.cs`, in order): forwarded headers (client IP from the gateway) → exception handler →
   status-code pages → CORS (off unless configured) → **authentication** (JWT bearer) → **rate limiter** (needs the
   user, so after authentication) → **authorization** (the endpoint's policy) → MVC.
2. **Idempotency filter** (`Idempotency/IdempotencyFilter.cs`), only on endpoints marked `[Idempotent]`: claims the key
   in Redis or answers from it.
3. **Controller** (`ProductsController.DecrementStock`): translates HTTP into a use-case call; nothing else.
4. **Use case** (Core, `StockCommandService`): checks the quantity, plans the change (`StockAdjustment`), asks the
   repository to apply it, reads the result, and returns `Result<ProductResponse>`: a value or an `Error`.
5. **Infrastructure** (`ProductRepository.TryAdjustStockAsync`): one SQL statement that changes the stock and writes the
   ledger entry; the caching decorator then invalidates the affected cache entries.
6. **Back in the controller** (`ApiControllerBase`): success → `200` with `ETag`; `Error` → status from its kind
   (`Validation` 400, `NotFound` 404, `Conflict` 409, `PreconditionFailed` 412, `Unprocessable` 422, `Unavailable` 503)
   and a problem-details body with the error's code.

## Data model

```
categories                      products                                   stock_movements (the ledger)
──────────                      ────────                                   ───────────────
id        int PK                id             int PK (100000–999999)      id            bigint PK (identity)
name      varchar unique  ◄──── category_id    int FK (restrict)    ◄───── product_id    int FK (cascade)
                                name           varchar(200)                quantity_change int (+/-)
                                description    varchar(2000) null          stock_after   int ≥ 0
                                price          numeric(9,2) 0.01–1,000,000 reason        varchar(20)
                                stock_quantity int 0–1,000,000             performed_by  varchar(100)
                                created_at, updated_at                     occurred_at   timestamptz
                                xmin (row version → ETag)                  request_key   varchar(300) null, unique
sequence product_id_seq: 100000 to 999999, no cycle
```

* **categories**: six fixed rows, inserted by the first migration (Spectacle Lenses, Frames, Sunglasses, Contact
  Lenses, Lens Care, Accessories). Read-only through the API.
* **products**: check constraints repeat the business limits (`ProductRules` is the single source for the validators,
  the entity and the constraints), so even a bug cannot store an invalid row.
* **stock_movements**: every change of stock is a row: the initial stock on create (`InitialStock`), each addition and
  removal, and a stock overwrite on update (`Correction`). For every product, the sum of its movements equals its stock.
  `request_key` holds the scoped idempotency key of the request that made the movement.

| Index | Serves |
| --- | --- |
| `ix_products_name` (GIN, `gin_trgm_ops`) | the case-insensitive contains-search; `pg_trgm` splits names into 3-letter pieces so `ILIKE '%frame%'` uses an index |
| `ix_products_name_id` (`name, id`) | the full list, ordered by name then id, page by page |
| `ix_products_stock_quantity_name_id` (`stock_quantity, name, id`) | the stock-level filter and its order |
| `ix_products_category_id` | the foreign key |
| `ix_categories_name` (unique) | category names are unique |
| `ix_stock_movements_product_id_occurred_at_id` (`product_id`, `occurred_at desc`, `id desc`) | a product's history, newest first |
| `ix_stock_movements_request_key` (unique, where not null) | the idempotency backstop |

Pages of the list and of the history are read with a "late row lookup": the page's ids come from the index first, then
only those rows are read, so deep pages stay fast on large tables (measured with 850,000 products and a 20-million-row
ledger). Per-table autovacuum settings (a migration) keep the visibility map current on the hot tables. The later
index migrations (`StockLevelIndex`, `StockMovementRequestKey`) build their indexes `CONCURRENTLY`, so the tables keep
accepting writes while they run.

Migrations (`ProductManagement.Infrastructure/Persistence/Migrations`): `InitialCreate`, `AddStockMovements` (with an
opening entry for existing products), `StockLevelIndex`, `StockMovementRequestKey`, `AutovacuumAtScale`,
`GrantAppPermissions` (grants the app user `pm_app` row access, if that role exists).

## Consistency

* **Stock changes are one SQL statement.** `UPDATE products SET stock_quantity = stock_quantity + Δ WHERE id = … AND
  stock_quantity BETWEEN <min> AND <max>` and the ledger `INSERT`, in one statement (`WITH updated AS (UPDATE …) INSERT
  …`). PostgreSQL locks the row and re-checks the condition against the latest committed value, so simultaneous
  removals queue up: two requests taking the last unit can never both succeed, and nothing is retried. If the
  condition fails, nothing is inserted; if the idempotency key's unique index rejects the insert, the update is rolled
  back with it. A BDD scenario proves it: 30 simultaneous orders of 1 unit on a stock of 10 → 10 succeed, 20 get 409,
  stock 0.
* **Edits use optimistic concurrency.** Load, change, save with the row version (`xmin`) as the concurrency token: a
  version that moved means someone else saved first (`412` with `If-Match`, `409` without).
* **Ids come from a sequence.** `nextval('product_id_seq')` is atomic across any number of API replicas, needs no
  coordination and never repeats an id, even after a delete. The seeded products draw from the same sequence.
* **No automatic database retries.** EF Core's retry-on-failure is off on purpose: a connection that dropped after a
  stock `UPDATE` committed would otherwise apply it twice. Retries are the client's, with an `Idempotency-Key`.

## Caching

The cache is added from outside, as **decorators** (wrappers with the same interface): `CachingProductQueries` wraps
the real read queries; `CacheInvalidatingUnitOfWork` and `CacheInvalidatingProductRepository` invalidate after writes.
Core does not know a cache exists. Without Redis the decorators pass straight through.

* **Generations instead of deletes.** Every cache key includes one or more change counters ("generations"); a write
  increments them, and old entries are never asked for again (they expire on their own). A version is read *before*
  the database, so a result computed during a concurrent write is stored under the old version, which nobody reads: a
  stale value can never become visible. There are three kinds: *data* (every write; stock-level lists), *listing*
  (creates, edits, deletes; which products a list or search contains and the counts), *product* (one per product; its
  details and history). A stock change therefore invalidates one product's entries and the stock-level lists, nothing
  else.
* **What is cached.** A single product (5 minutes), stock-level pages and histories (1 minute), the ids and counts of
  the full list and of searches (10 minutes; they are invalidated explicitly). List rows themselves are re-read from the
  database by id every time (cheap), so the stock shown is always current.
* **Single flight.** Simultaneous misses for the same key share one database query per replica (no stampede when a
  popular entry expires).
* **Fail open.** Every Redis call has a 500 ms budget. If Redis is slow or down, reads go to the database, rate limits
  fall back to per-replica counters, the failed-sign-in throttle lets requests through, and `/health/ready` reports
  `Degraded`, not `Unhealthy`. Only requests that carry an `Idempotency-Key` are refused (`503`), because exactly-once
  can no longer be guaranteed.
* Redis is shared: every key starts with `Cache:KeyPrefix` (`product-management`). Redis evicts only keys with a TTL
  (`volatile-lru`), so the generation counters survive memory pressure.

The gateway adds a 1-second microcache in front of the API, and the web app a short browser cache; both are described
in the frontend README.

## Security

* **Least privilege in the database.** The API connects as `pm_app`, which may read and write rows but not change the
  schema; its statements end after 30 s and idle transactions after 60 s. Migrations run as `pm_migrator`, the schema
  owner, from the deploy repository (`scripts/migrate.sh`), before each rollout. Both users are created by the deploy
  repository's `scripts/create-database.sh`.
* **Hashed client secrets.** PBKDF2-HMAC-SHA256, 100,000 iterations, 16-byte random salt, 32-byte hash, stored as
  `pbkdf2-sha256$100000$<salt>$<hash>` (Base64). `hash-secret` makes one. Comparison is constant-time.
* **Asymmetric tokens.** Only the API holds the private key; everyone else verifies with the public key (JWKS).
* **No secrets in git or in configuration files.** Locally they are in .NET user-secrets; in Kubernetes the signing
  key and the clients come from the Secret `api-secrets`, the database password from `PGPASSWORD` and the Redis
  password from `Redis__Password` (both from Secrets), never inside a connection string. The API refuses to start
  without a valid signing key or with a client that has no well-formed hash.
* **Strict input.** Unknown JSON types are refused (`"12"` is not a number), errors never echo internal type names, and
  request bodies are limited (1 MB at the gateway).
* **Trust boundary.** The API trusts `X-Forwarded-For` because only the gateway can reach it (a Kubernetes
  NetworkPolicy, enforced on k3s).
* **Container.** Non-root user, read-only root filesystem, all Linux capabilities dropped (set in the deploy
  repository).

## Configuration

ASP.NET Core reads configuration from `appsettings.json`, `appsettings.<Environment>.json`, user-secrets (Development
only), `config/consumers.json` (optional) and environment variables, later sources winning. An environment variable
`Section__Key` sets `Section:Key`. Invalid values stop the API at startup.

| Key | Default | Meaning |
| --- | --- | --- |
| `ConnectionStrings:ProductManagement` | `""` (required) | PostgreSQL (Npgsql). Without `Password`, the driver uses `PGPASSWORD` |
| `ConnectionStrings:Redis` | `""` | Redis (StackExchange.Redis). Required outside Development and the test host; without it the cache is off and limits/idempotency are per process |
| `Redis:Password` | none | the Redis password on its own (wins over one in the connection string) |
| `Cache:KeyPrefix` | `product-management` | prefix of every Redis key |
| `Cache:ProductTtl` | `00:05:00` | a cached product |
| `Cache:ListTtl` | `00:01:00` | a cached stock-level page or history page |
| `Cache:ListingTtl` | `00:10:00` | cached ids and counts of lists and searches |
| `Cache:ProductGenerationTtl` | `01:00:00` | a product's change counter |
| `Cache:ConnectTimeout`, `Cache:OperationTimeout` | `00:00:02`, `00:00:00.5` | Redis connect and per-command budget |
| `Database:ApplyMigrationsOnStartup` | `false` (Development: `true`) | migrate when the API starts; deployments migrate before the rollout instead |
| `Database:SeedSampleData` | `false` (Development: `true`) | 24 sample products when the database has none (runs after migrations) |
| `Authentication:Issuer`, `Authentication:Audience` | `product-management-api` | `iss` and `aud` written into and required from every token |
| `Authentication:SigningKey` | `""` (required) | the ES256 private key, PEM (`generate-signing-key`) |
| `Authentication:AccessTokenLifetime` | `01:00:00` | token lifetime (1 s to 1 day) |
| `Authentication:Consumers` | `[]` | `[{ "ClientId", "SecretHash", "Roles": [...] }]` |
| `RateLimiting:Enabled` | `true` | switch all rate limits off (tests only) |
| `RateLimiting:RequestsPerSecond`, `RequestBurst` | `50`, `100` | per caller, all endpoints |
| `RateLimiting:WritesPerSecond`, `WriteBurst` | `10`, `20` | per caller, writes |
| `RateLimiting:TokenRequestsPerMinute` | `20` | per client IP, token endpoint, per replica |
| `SignInThrottle:MaxFailures`, `SignInThrottle:Window` | `10`, `00:01:00` | failed sign-ins per client IP before blocking |
| `Idempotency:KeyLifetime` | `1.00:00:00` (24 h) | how long a completed key's response is replayed |
| `Idempotency:InProgressTimeout` | `00:01:00` | how long a running request holds its key (a replica that dies mid-request releases it) |
| `OpenApi:Enabled` | `false` (Development: `true`) | Swagger UI and the OpenAPI document |
| `Cors:AllowedOrigins` | `[]` (Development: `http://localhost:4200`) | browser origins allowed to call the API directly; empty when served through the gateway |
| `Logging:*` | JSON console logs, UTC timestamps, scopes (trace id) included | Development: plain text and the SQL of each command |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` loads `appsettings.Development.json` and user-secrets; anything but `Development`/`Testing` requires Redis |
| `ASPNETCORE_HTTP_PORTS` | `8080` in the image | the HTTP port |

What the Kubernetes deployment sets (the deploy repository's `apps/backend/deployment.yaml` and
`environments/<env>/api.yaml`): `ASPNETCORE_ENVIRONMENT=Production`, `Authentication__AccessTokenLifetime=01:00:00`,
`Database__ApplyMigrationsOnStartup=false`, `OpenApi__Enabled` (`true` locally and in staging), the five
`RateLimiting__*` values above, `SignInThrottle__MaxFailures=10`, `SignInThrottle__Window=00:01:00`,
`Idempotency__KeyLifetime=01:00:00`, `Idempotency__InProgressTimeout=00:01:00`, the two connection strings
(`Username=pm_app;Maximum Pool Size=10`), and from Secrets `PGPASSWORD`, `Redis__Password`,
`Authentication__SigningKey` and the file `/app/config/consumers.json`.

## The image and its commands

`docker build -t product-management/api .` builds the image every environment runs (SDK 10.0 to build,
`mcr.microsoft.com/dotnet/aspnet:10.0` to run, as the non-root `app` user, port 8080). Besides serving HTTP, the
image has three commands; each runs and exits:

| Command | Does | Used by |
| --- | --- | --- |
| `migrate` | applies pending migrations (and the sample data when `Database:SeedSampleData` is true), exit code 1 on failure; needs only the database (and Redis to invalidate the cache) | the deploy repository's `scripts/migrate.sh`, which its `scripts/deploy.sh` runs for every environment before applying it: a one-off pod with the migrator's credentials and the API version the environment names (with Argo CD: the PreSync hook in its `argocd/hooks/`) |
| `generate-signing-key` | prints a new ES256 (P-256) private key, PKCS#8 PEM | setting up an environment (`API_SIGNING_KEY`) |
| `hash-secret <secret>` | prints the `SecretHash` for a client secret | adding a client to `consumers.json` |

```bash
docker run --rm product-management/api generate-signing-key
docker run --rm product-management/api hash-secret <secret>
```

### Outside Kubernetes: docker run, Docker Compose

The published image (`registry.gitlab.com/zeiss/backend/api:<version>`) is a plain OCI image with no Kubernetes in
it: it runs on Kubernetes, with `docker run`, Docker Compose or Docker Swarm, anywhere it can reach a PostgreSQL and a
Redis. Everything it needs is environment variables (and one optional file):

| Variable | Required | What |
| --- | --- | --- |
| `ConnectionStrings__ProductManagement` | yes | PostgreSQL, without the password: `Host=...;Port=5432;Database=...;Username=...` |
| `PGPASSWORD` | yes | the database password (read by the driver; never inside the connection string) |
| `ConnectionStrings__Redis` | yes (outside `Development`) | Redis, `host:6379` |
| `Redis__Password` | when Redis has one | the Redis password |
| `Authentication__SigningKey` | yes | the ES256 private key, PEM (`generate-signing-key`) |
| `/app/config/consumers.json` | yes (or `Authentication__Consumers__<n>__*` variables) | the accounts: `{"Authentication":{"Consumers":[{"ClientId":"...","SecretHash":"...","Roles":["viewer"]}]}}` (`hash-secret`) |
| `Database__SeedSampleData` | no | `true`: the 24 sample products on an empty database (`migrate` only) |
| `OpenApi__Enabled` | no | `true`: Swagger UI on `/swagger` |

It listens on port 8080 as the non-root user `app` (uid 1654). Apply the migrations first, with the same image and the
schema owner's credentials (`migrate` exits when it is done), then start the API. Every other setting is in
[Configuration](#configuration).

```bash
image=registry.gitlab.com/zeiss/backend/api:1.4.0
docker run --rm "$image" generate-signing-key > signing-key.pem
printf '{"Authentication":{"Consumers":[{"ClientId":"viewer","SecretHash":"%s","Roles":["viewer"]}]}}\n' \
  "$(docker run --rm "$image" hash-secret viewer)" > consumers.json             # one account: viewer / viewer

docker run --rm -e ConnectionStrings__ProductManagement="Host=db.example.internal;Port=5432;Database=product_management;Username=pm_migrator" \
  -e PGPASSWORD="$MIGRATOR_PASSWORD" -e ConnectionStrings__Redis=redis.example.internal:6379 -e Redis__Password="$REDIS_PASSWORD" \
  "$image" migrate
docker run -d --name api -p 5080:8080 \
  -e ConnectionStrings__ProductManagement="Host=db.example.internal;Port=5432;Database=product_management;Username=pm_app" \
  -e PGPASSWORD="$APP_PASSWORD" -e ConnectionStrings__Redis=redis.example.internal:6379 -e Redis__Password="$REDIS_PASSWORD" \
  -e Authentication__SigningKey="$(cat signing-key.pem)" -v "$PWD/consumers.json:/app/config/consumers.json:ro" \
  "$image"
curl http://localhost:5080/health/ready
```

With Docker Compose, the whole system on one machine (the frontend README has the `web` and `gateway` services that
complete it); `API_SIGNING_KEY="$(cat signing-key.pem)" docker compose up -d`:

```yaml
services:
  postgres:
    image: postgres:18-alpine
    environment: { POSTGRES_USER: product_management, POSTGRES_PASSWORD: change-me, POSTGRES_DB: product_management }
    healthcheck: { test: ["CMD", "pg_isready", "-U", "product_management"], interval: 2s, retries: 30 }
  redis:
    image: redis:8-alpine
    command: ["redis-server", "--requirepass", "change-me-too"]
  migrate:                        # runs once, before the API: the schema of exactly this version
    image: registry.gitlab.com/zeiss/backend/api:1.4.0
    command: ["migrate"]
    environment: &api-env
      ConnectionStrings__ProductManagement: "Host=postgres;Port=5432;Database=product_management;Username=product_management"
      PGPASSWORD: change-me
      ConnectionStrings__Redis: "redis:6379"
      Redis__Password: change-me-too
      Database__SeedSampleData: "true"
    depends_on: { postgres: { condition: service_healthy }, redis: { condition: service_started } }
  api:
    image: registry.gitlab.com/zeiss/backend/api:1.4.0
    environment:
      <<: *api-env
      Authentication__SigningKey: ${API_SIGNING_KEY:?the PEM of generate-signing-key}
    volumes: ["./consumers.json:/app/config/consumers.json:ro"]
    depends_on: { migrate: { condition: service_completed_successfully } }
```

This minimal file uses one database user for brevity; in Kubernetes the API connects as `pm_app` (rows only) and only
the migrations as the schema owner (deploy repository, `scripts/create-database.sh`). A Swarm stack takes the same
file (`docker stack deploy`), with the key and the accounts as Docker secrets instead of a variable and a bind mount.

## Run locally

### Prerequisites

| Tool | For |
| --- | --- |
| .NET 10 SDK, 10.0.401 or a later feature band (`global.json`) | building, tests, `dotnet run` |
| Docker | the tests (Testcontainers), a local PostgreSQL, the image |

| | .NET 10 SDK | Docker |
| --- | --- | --- |
| macOS | `brew install dotnet`, or the installer from https://dotnet.microsoft.com/download/dotnet/10.0 | Docker Desktop |
| Windows | `winget install Microsoft.DotNet.SDK.10` | Docker Desktop (WSL 2 backend) |
| Linux (Ubuntu) | `curl -sSL https://dot.net/v1/dotnet-install.sh \| bash -s -- --channel 10.0`, then add `~/.dotnet` to `PATH` (Ubuntu's own `dotnet-sdk-10.0` packages are 10.0.1xx, too old) | Docker Engine (`sudo apt install docker.io`, or the deploy repository's `scripts/setup-ubuntu.sh`) |

The commands are the same on every platform (on Windows in PowerShell or Git Bash, or in WSL 2 with the SDK installed
there; the `bash` snippets need a bash shell).

### With dotnet run

The API runs in the `Development` environment (`Properties/launchSettings.json`, http://localhost:5080). There it
connects to a PostgreSQL on `localhost:5432` (user, password and database `product_management`), applies the
migrations and seeds the sample catalogue at startup, and without a Redis connection string uses in-memory
replacements (cache off; idempotency keys, rate limits and sign-in failures per process).

**1. A PostgreSQL**, e.g. in Docker:

```bash
docker run -d --name dev-postgres -p 5432:5432 \
  -e POSTGRES_USER=product_management -e POSTGRES_PASSWORD=product_management -e POSTGRES_DB=product_management \
  postgres:18-alpine
```

**2. A signing key and the demo clients**, once, in the API's .NET user-secrets (never in a file of the repository):

```bash
dotnet build ProductManagement.Api
api() { dotnet run --project ProductManagement.Api --no-build --no-launch-profile -- "$@"; }
dotnet user-secrets set --project ProductManagement.Api Authentication:SigningKey "$(api generate-signing-key)"
i=0
for account in viewer:viewer clerk:stock-clerk manager:manager; do           # client id (= its secret) : role
  dotnet user-secrets set --project ProductManagement.Api "Authentication:Consumers:$i:ClientId" "${account%%:*}"
  dotnet user-secrets set --project ProductManagement.Api "Authentication:Consumers:$i:SecretHash" "$(api hash-secret "${account%%:*}")"
  dotnet user-secrets set --project ProductManagement.Api "Authentication:Consumers:$i:Roles:0" "${account##*:}"
  i=$((i + 1))
done
```

(With the deploy repository cloned next to this one, its `scripts/local-secrets.sh` does the same.)

**3. Run:**

```bash
dotnet run --project ProductManagement.Api           # http://localhost:5080, Swagger: http://localhost:5080/swagger
```

Demo accounts: `viewer`/`viewer`, `clerk`/`clerk`, `manager`/`manager` (roles `viewer`, `stock-clerk`, `manager`).
Optionally a Redis as in the cluster: `docker run -d --name dev-redis -p 6379:6379 redis:8-alpine`, then run with
`ConnectionStrings__Redis=localhost:6379`. The web app's dev server (`npm start` in frontend) proxies `/api` to
http://localhost:5080.

**Against the local cluster's PostgreSQL and Redis** (after the deploy repository's `scripts/deploy.sh local`), as the
schema owner, since Development applies the migrations; stop `dev-postgres` first, it uses the same port:

```bash
kubectl -n infra port-forward svc/postgres 5432:5432 &
kubectl -n infra port-forward svc/redis 6379:6379 &
PGPASSWORD="$(kubectl -n product-management get secret database-credentials -o go-template='{{ index .data "migrator-password" | base64decode }}')" \
ConnectionStrings__ProductManagement="Host=localhost;Port=5432;Database=product_management;Username=pm_migrator" \
ConnectionStrings__Redis=localhost:6379 \
Redis__Password="$(kubectl -n infra get secret redis-auth -o go-template='{{ .data.password | base64decode }}')" \
dotnet run --project ProductManagement.Api
```

**The whole system** (API, web app, gateway, PostgreSQL, Redis, logs) on Docker Desktop's Kubernetes (macOS, Windows)
or k3s on an Ubuntu server: clone the three repositories side by side and follow the deploy repository's README
(`scripts/infra.sh up`, then `scripts/deploy.sh local`, which builds this repository's image).

## Tests

`ProductManagement.sln` has the solution folders `Source` and `Tests`. The tests use xUnit v3 on Microsoft.Testing
Platform (`global.json` selects it).

| Project | Tests | What it proves |
| --- | --- | --- |
| `ProductManagement.UnitTests` | 216 | **Core:** product rules and entity invariants, stock adjustment planning, every validator, the use cases with fakes (results, error codes), `Result`. **Infrastructure:** cache decorators and invalidation, generations, single flight, Redis failure handling and in-memory fallbacks, service registration (Redis required outside Development). **Api:** ETag parsing, idempotency key and request fingerprint, the shared token bucket, secret hashing, token service and signing-key validation |
| `ProductManagement.Api.IntegrationTests` | 153 | real HTTP against the real API with **PostgreSQL 18 and Redis 8 in containers** (Testcontainers): CRUD, queries and paging, stock endpoints and the ledger, concurrency (no overselling, ETag/If-Match), idempotency (replay, 409/422/503, the database backstop), the cache, the token endpoint and sign-in throttle, authorization per role, rate limits, problem-details shapes, sample-data seeding, query plans at scale |
| `ProductManagement.Api.AcceptanceTests` | 16 | BDD scenarios in plain language (Reqnroll, `Features/*.feature`): product catalogue (unique ids, validation naming each problem, browsing, search, low stock, conflicting edits, update, delete) and stock management (removing, rejecting overselling, receiving, positive quantities, 30 simultaneous orders on 10 units, unknown products) |
| `ProductManagement.Testing` | | shared test infrastructure: the containers, the API factory, test tokens, a typed API client |

```bash
dotnet test --solution ProductManagement.sln                                    # every suite; Docker must be running
dotnet test --project ProductManagement.UnitTests                               # one project (no Docker needed)
dotnet test --project ProductManagement.UnitTests --filter-class "*EntityTagTests"   # one class
dotnet format ProductManagement.sln --verify-no-changes                         # formatting, as CI checks it
dotnet build ProductManagement.sln --configuration Release                      # warnings are errors
```

The integration and acceptance tests start PostgreSQL and Redis containers themselves; nothing else needs to run.

## CI

[`.gitlab-ci.yml`](.gitlab-ci.yml). The pipeline only builds and publishes: it tests, builds and scans the image, and
for a version tag publishes that version and creates a GitLab Release. It never touches an environment or the deploy
repository: the deploy repository chooses which published version each environment runs (its
`environments/<env>/kustomization.yaml`), applies the database migrations with that image and rolls it out (the
"pull" model, see its README).

| Job | Stage | When | Does |
| --- | --- | --- | --- |
| `test:backend` | test | every pipeline | restore, format check, Release build (warnings are errors), every test suite (Testcontainers on Docker-in-Docker), JUnit report |
| GitLab SAST, Secret Detection | test | every pipeline | GitLab's own analysers (included templates) |
| `build:api` | build | every pipeline | builds and pushes `registry.gitlab.com/zeiss/backend/api:<short sha>` (BuildKit, layer cache in the registry); in a tag pipeline it reuses that image when the pipeline on `main` already built it |
| `migrations:sql` | build | every pipeline | the idempotent SQL script of all migrations (`dotnet ef migrations script --idempotent`), as an artifact to review in the merge request |
| `scan:api` | scan | every pipeline | Trivy: full report to GitLab; fails on fixable critical vulnerabilities |
| `publish:api` | publish | tag `vX.Y.Z` or `vX.Y.Z-rc.N` only | after test and scan passed: `crane tag` adds the tag `X.Y.Z` (no `v`) to the same image, so the version has the commit build's digest; the digest goes to `release` |
| `release` | release | tag `vX.Y.Z` or `vX.Y.Z-rc.N` only | GitLab's `release:` keyword (image `release-cli`): the Release `vX.Y.Z`, with the image reference and digest and the commits since the previous version ([`ci/release-notes.sh`](ci/release-notes.sh)) |

Which pipelines run: merge requests and `main` (test, build, scan), and tags that match
`v<MAJOR>.<MINOR>.<PATCH>` with an optional `-rc.<N>`; any other tag starts no pipeline. Images on `main` are
`api:<short sha>`: builds of a commit, which the deploy repository never picks up by itself. Only `api:X.Y.Z` tags are
versions.

**`ci/release-notes.sh`** (the same in frontend; POSIX `sh`, because the release-cli image has no bash):
`ci/release-notes.sh <tag> <image reference>...` prints the Release's description in Markdown: the images with their
digests, then `git log --no-merges` since the previous version tag (for a release `X.Y.Z` the previous release,
skipping release candidates; for a release candidate the previous tag of any kind) and a compare link. To see what
it would write: `ci/release-notes.sh v1.4.0 registry.gitlab.com/zeiss/backend/api:1.4.0` in a clone with the tags.

**Migrations must be backward compatible** (expand/contract): the deploy repository runs them while the previous
version is still serving, and next to the new one during the rollout. A rollback (choosing an older version there)
never touches the schema; a bad migration is fixed forward.

**No CI/CD variable is needed in this project:** the jobs use only what GitLab provides (`CI_REGISTRY_USER` /
`CI_REGISTRY_PASSWORD` for the registry, the job token for the release). Two project settings matter:

* **Protected tags** (Settings → Repository → Protected tags): `v*`, allowed to create: Maintainers. Only maintainers
  can then release, and the publish and release jobs run with protected-branch rights.
* **Tag immutability** of the container registry (Settings → Packages and registries → Container registry, protected
  or immutable tags): a published version can never be overwritten or deleted.

## How to release a version

A version is a git tag on a commit of `main` that passed its pipeline. Versions follow
[Semantic Versioning](https://semver.org): `MAJOR.MINOR.PATCH`.

| Change since the previous version | Raise | Example |
| --- | --- | --- |
| a breaking change for clients: an endpoint, field, error code or behaviour removed or changed incompatibly (a client must change) | MAJOR | 1.4.0 → 2.0.0 |
| new, backward compatible functionality: a new endpoint, an optional field, a new setting with a default | MINOR | 1.4.0 → 1.5.0 |
| backward compatible fixes only (bugs, performance, security patches, dependencies) | PATCH | 1.4.0 → 1.4.1 |
| a version to try first (a release candidate) | `-rc.N` | 1.5.0-rc.1, then 1.5.0 |

A database migration does not by itself make a major version: migrations are always backward compatible
(expand/contract). Removing something clients use does.

1. Merge the changes into `main` and wait for its pipeline (it builds `api:<short sha>`).
2. Create the tag on that commit, from the UI (Code → Tags → New tag: name `v1.4.0`, create from `main`, a message) or
   from a clone:

   ```bash
   git switch main && git pull
   git tag -a v1.4.0 -m "v1.4.0: stock history export"
   git push origin v1.4.0
   ```

3. The tag's pipeline tests and scans that commit again, publishes `registry.gitlab.com/zeiss/backend/api:1.4.0`
   (`publish:api`) and creates the Release `v1.4.0` (Deploy → Releases), with the image, its digest and the changes.
4. Nothing else here: staging picks `1.4.0` up by itself (the deploy repository's scheduled `update:staging` job takes
   the newest `X.Y.Z`; release candidates only when someone sets them), and production changes through a merge request
   in the deploy repository (`scripts/set-version.sh production api 1.4.0`). Its README has the details.

A tag is never moved or reused: a fix is a new version (`v1.4.1`). Delete a wrong tag only before its pipeline
published it.

## Design decisions

| Decision | Why |
| --- | --- |
| Three projects with a compile-time dependency rule | business rules cannot depend on the database or HTTP; each layer can be tested on its own |
| Expected failures returned as `Result<T>` / `Error`, mapped in one place | the ways an operation can fail are visible in its signature; HTTP status and `errorCode` are consistent across endpoints; exceptions stay for bugs and outages |
| Stock changes as one conditional SQL statement | no overselling under any concurrency, no retries, one round trip |
| Ids from a PostgreSQL sequence | unique across replicas without coordination; never reused |
| A stock ledger written in the same statement | a full history, and the database backstop for idempotency |
| Idempotency keys in Redis with a unique key in the ledger | fast replays across replicas; correctness even when Redis forgets |
| Cache as decorators with generations | Core stays unaware of Redis; a write invalidates only what it affects; no stale read after a write |
| Fail open on Redis | a cache outage costs speed, not availability |
| The API issues its own ES256 tokens with a public JWKS | no external identity provider needed for registered clients; the gateway verifies without a secret |
| Permissions, not roles, on endpoints | the role mapping lives in one place |
| Migrations as a command of the same image, run before each rollout by the deploy repository | the schema and the code that needs it come from the same build; replicas never race to migrate; the app user never needs schema rights |
| Versions as SemVer tags; the pipeline only publishes, the deploy repository pulls | this repository needs no access to any environment or to the deploy repository; a version is published only after its tests and scan; the same image digest from `main` to production; Argo CD Image Updater can follow the same tags |
| Problem details (RFC 9457) with `errorCode` | one standard error shape that clients can branch on |

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `A compatible .NET SDK was not found. Requested SDK version: 10.0.401` | an older SDK, or another `dotnet` first on the `PATH` | install SDK 10.0.401+; check `dotnet --list-sdks` |
| startup fails: `Connection string 'ProductManagement' is not configured` | no database configured outside Development | set `ConnectionStrings__ProductManagement` |
| startup fails: `Connection string 'Redis' is not configured ...` | `ASPNETCORE_ENVIRONMENT` is not `Development` and there is no Redis | set `ConnectionStrings__Redis`, or run as Development |
| startup fails on `Authentication` (signing key or consumer) | no or invalid signing key, or a client without a well-formed `SecretHash` | step 2 of [With dotnet run](#with-dotnet-run), or `generate-signing-key` / `hash-secret` |
| `dotnet run` fails to connect to PostgreSQL | no database on `localhost:5432` | start `dev-postgres` (step 1) |
| port 5432 already in use | `dev-postgres` and a port-forward (or another PostgreSQL) at once | stop one of them |
| every request `401` | missing `Authorization: Bearer`, or the token expired (1 hour) | sign in again at `/api/auth/token` |
| sign-in `429 too_many_failed_attempts` | 10 failed sign-ins from your IP within a minute | wait for `Retry-After` |
| `503 idempotency_unavailable` | Redis unreachable and the request has an `Idempotency-Key` | fix Redis; retry with the same key |
| integration tests fail at startup | Docker is not running (Testcontainers) | start Docker |
| `migrate` fails with `permission denied for schema public` | it ran as `pm_app` | run it as the schema owner (`pm_migrator`), as the deploy repository's `scripts/migrate.sh` does |
| a tag starts no pipeline | the tag is not `vX.Y.Z` / `vX.Y.Z-rc.N` (e.g. `1.4.0` without `v`, or `v1.4`) | delete it and create `v1.4.0` |
| `publish:api` fails: the tag cannot be overwritten | the version was published before (tag immutability) | versions are never reused: release `v1.4.1` |
| `release` fails with `already exists` | a Release with that tag exists (a re-run) | nothing to do, or delete the Release in Deploy → Releases and retry the job |
| the deploy repository does not see a new version | `publish:api` did not run (the tag pipeline failed before it), or the registry token there cannot read this registry | check the tag's pipeline; in deploy, `scripts/versions.sh api` |
