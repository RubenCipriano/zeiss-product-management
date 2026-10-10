# Product Management — API

The backend of the Product Management system: a .NET 10 / ASP.NET Core Web API with EF Core and PostgreSQL, a Redis
read cache, JWT authentication (tokens issued by the API to registered consumers), role-based authorization, rate
limiting and idempotency keys. This repository holds the code, the tests, the image and its CI pipeline. The
Kubernetes manifests and the environments are in the [deploy](https://gitlab.com/zeiss/deploy) repository; the web
app and the gateway in [frontend](https://gitlab.com/zeiss/frontend).

## What it does

The assessment asked for a product API; all of it is here and tested over real HTTP against a real PostgreSQL:

| Requirement | How |
| --- | --- |
| CRUD: `GET/POST /api/products`, `GET/PUT/DELETE /api/products/{id}` | lists are paged (`page`, `pageSize` ≤ 100) and every item carries its stock |
| `POST /api/products/{id}/decrement-stock/{quantity}` and `…/add-to-stock/{quantity}` | `200 OK` with the updated product; never below 0 (`409`), quantity validated (`400`) |
| `GET /api/products/search?name=` | partial, case-insensitive match (trigram index; `%` and `_` are literal) |
| `GET /api/products/stock-level?min=&max=` | inclusive range, either bound optional |
| Unique 6-digit ids, also with several instances | a PostgreSQL sequence 100000–999999: every instance draws from the same counter; `503 product.ids_exhausted` when used up |
| Validation on create and update | FluentValidation, every invalid field named in one RFC 9457 problem-details response |
| EF Core code-first migrations and seeding | migrations in `ProductManagement.Infrastructure/Persistence/Migrations`; categories seeded by migration, a sample catalogue on an empty database |
| Unit tests (BDD optional) | 385 tests: 216 unit, 153 integration (Testcontainers), 16 BDD scenarios (Reqnroll) |

Added because a real deployment needs them: sign-in with ES256 JWTs issued by the API to registered consumers and
three roles (viewer, stock clerk, manager); optimistic concurrency with `ETag` / `If-Match` (`412` instead of a lost
update); `Idempotency-Key` so a retried request is applied once (Redis first, a unique key in the stock ledger as the
backstop); a stock ledger with each product's history (`GET /api/products/{id}/stock-movements`); a Redis read cache;
per-user rate limits shared by every replica; health checks (`/health/live`, `/health/ready`); JSON logs carrying the
request's trace id.

## Design decisions

* **Three projects, one rule.** `Core` (what the system does: entities, rules, use cases), `Infrastructure` (how it
  is stored: EF Core, PostgreSQL, Redis) and `Api` (how it is reached: HTTP, security). `Core` does not reference the
  database library, so business rules cannot depend on it; the compiler enforces it.
* **Stock changes are one SQL statement.** A conditional `UPDATE … WHERE stock + change BETWEEN 0 AND max` plus the
  ledger insert, in one round trip: two requests taking the last unit can never both succeed, and nothing retries.
* **Expected failures are returned, not thrown.** Use cases return `Result<T>` / `Error`; one place maps the error
  type to an HTTP status and a stable `errorCode`. Exceptions are left for bugs and outages.
* **Ids come from the database.** A sequence is atomic across any number of API instances, needs no coordination and
  never repeats an id, even after a delete.
* **The cache is added from outside.** `CachingProductQueries` wraps the real queries (a decorator); `Core` knows
  nothing about Redis. Writes bump a version number instead of deleting keys, so a stale read can never be cached
  after a change. If Redis is down the API keeps working, slower.
* **Least privilege in the database.** The API connects as a user that can read and write rows but not change the
  schema; migrations run as the schema owner, from the pipeline, before the new version is released.

## Projects

`ProductManagement.sln`, with the solution folders `Source` and `Tests`:

| Project | What it is |
| --- | --- |
| `ProductManagement.Core` | Domain and use cases, by feature (`Products/`, `Categories/`, `Common/`). No EF Core, no ASP.NET Core. |
| `ProductManagement.Infrastructure` | EF Core `DbContext`, configurations, migrations, repositories and queries, the id generator, the Redis cache decorator, seeding, health checks. |
| `ProductManagement.Api` | Controllers, HTTP mapping (status codes, ETag/If-Match, problem details), token endpoint, JWT verification, authorization, rate limiting, idempotency, OpenAPI, health endpoints, and the `migrate`, `hash-secret` and `generate-signing-key` commands. `ProductManagement.Api.http` has a request for every endpoint. |
| `ProductManagement.UnitTests` | Unit tests, in the folders `Core/`, `Infrastructure/` and `Api/`. |
| `ProductManagement.Api.IntegrationTests` | Real HTTP against real PostgreSQL 18 and Redis 8 (Testcontainers). |
| `ProductManagement.Api.AcceptanceTests` | BDD scenarios (Reqnroll / Gherkin, `Features/*.feature`). |
| `ProductManagement.Testing` | Shared test infrastructure (the containers, the API factory, test tokens). |

Other files: `Dockerfile` (the API image, which also runs the migrations), `global.json` (SDK 10.0.401+),
`Directory.Build.props` / `Directory.Packages.props` (shared build settings, central package versions),
`dotnet-tools.json` (`dotnet-ef`), `ci/` (the scripts of the pipeline's migrate and release jobs).

## Prerequisites

| Tool | For |
| --- | --- |
| .NET 10 SDK (10.0.401+, `global.json`) | building, tests, `dotnet run` |
| Docker | the tests (Testcontainers), a local PostgreSQL, the image |

Installing them (the SDK must be 10.0.401 or a later feature band; Linux distributions' own `dotnet-sdk-10.0`
packages are 10.0.1xx and too old):

| | .NET 10 SDK | Docker |
| --- | --- | --- |
| macOS | `brew install dotnet`, or the installer from https://dotnet.microsoft.com/download/dotnet/10.0 | Docker Desktop |
| Windows | `winget install Microsoft.DotNet.SDK.10` | Docker Desktop (WSL 2 backend) |
| Linux (Ubuntu) | `curl -sSL https://dot.net/v1/dotnet-install.sh \| bash -s -- --channel 10.0`, then add `~/.dotnet` to `PATH` | Docker Engine (`sudo apt install docker.io`, or the deploy repository's `scripts/setup-ubuntu.sh`) |

The commands below are the same on every platform (on Windows, in PowerShell or Git Bash, or in WSL 2 with the SDK
installed there; the `bash` snippets need a bash shell).

## Run locally with `dotnet run`

The API runs in the `Development` environment (`Properties/launchSettings.json`, http://localhost:5080). There,
`appsettings.Development.json` connects to a PostgreSQL on `localhost:5432` with the user, password and database
`product_management`, applies the migrations and seeds the sample catalogue on start-up, and without a Redis
connection string uses in-memory replacements (cache, idempotency keys, rate limits).

**1. A PostgreSQL** with those settings, e.g. in Docker:

```bash
docker run -d --name dev-postgres -p 5432:5432 \
  -e POSTGRES_USER=product_management -e POSTGRES_PASSWORD=product_management -e POSTGRES_DB=product_management \
  postgres:18-alpine
```

**2. A token signing key and the demo consumers**, once, in the API's .NET user-secrets (never in a file of the
repository). The API's own commands make them:

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
`ProductManagement.Api.http` signs in and calls every endpoint. Optional, a Redis as in the cluster:
`docker run -d --name dev-redis -p 6379:6379 redis:8-alpine`, then run with `ConnectionStrings__Redis=localhost:6379`.

**Against the cluster's PostgreSQL and Redis** (after the deploy repository's `scripts/deploy.sh local`), as the schema
owner, since Development applies the migrations; stop `dev-postgres` first, it uses the same port:

```bash
kubectl -n infra port-forward svc/postgres 5432:5432 &
kubectl -n infra port-forward svc/redis 6379:6379 &
PGPASSWORD="$(kubectl -n product-management get secret database-credentials -o go-template='{{ index .data "migrator-password" | base64decode }}')" \
ConnectionStrings__ProductManagement="Host=localhost;Port=5432;Database=product_management;Username=pm_migrator" \
ConnectionStrings__Redis=localhost:6379 \
Redis__Password="$(kubectl -n infra get secret redis-auth -o go-template='{{ .data.password | base64decode }}')" \
dotnet run --project ProductManagement.Api
```

**The whole system** (API, web app, gateway, PostgreSQL, Redis, logs) on Docker Desktop's Kubernetes (macOS,
Windows) or a k3s on an Ubuntu server: clone the three repositories side by side and follow the deploy repository's
README, "Where it runs" (`scripts/infra.sh up`, then `scripts/deploy.sh local`, which builds this folder's image).

## Tests

```bash
dotnet test --solution ProductManagement.sln        # every suite; Docker must be running
```

The integration and acceptance tests start PostgreSQL and Redis containers themselves (Testcontainers); nothing else
needs to be running. Format and build exactly as CI does:

```bash
dotnet format ProductManagement.sln --verify-no-changes
dotnet build ProductManagement.sln --configuration Release      # warnings are errors
```

## The image and its commands

`docker build -t product-management/api .` builds the image that every environment runs. Besides serving HTTP it has
three commands: `migrate` (applies the migrations and exits; the pipeline runs it before a release),
`generate-signing-key` (a new ES256 private key, PEM) and `hash-secret <secret>` (a consumer's `SecretHash`):

```bash
docker run --rm product-management/api generate-signing-key
docker run --rm product-management/api hash-secret <secret>
```

Configuration is environment variables (ASP.NET Core reads `Section__Key` as `Section:Key`); the deployed values are in
the deploy repository (`apps/backend/deployment.yaml`, and `environments/<environment>/api.yaml` for the connection
strings). Passwords are never in configuration: `PGPASSWORD` and `Redis__Password` come from Secrets in the cluster,
the signing key and the consumers from the Secret `api-secrets`.

## CI (GitLab)

[`.gitlab-ci.yml`](.gitlab-ci.yml). The pipeline builds, checks and releases the image; it never applies Kubernetes
manifests. An environment runs the API image whose tag is in the deploy repository's
`environments/<environment>/kustomization.yaml`, and the deploy repository's pipeline applies it.

| Job | When | Does |
| --- | --- | --- |
| `test:backend` | every pipeline | format check, Release build, every test suite (Testcontainers on Docker-in-Docker) |
| `build:api` | every pipeline | builds and pushes `registry.gitlab.com/zeiss/backend/api:<sha>` (BuildKit, registry layer cache) |
| `migrations:sql` | every pipeline | the idempotent SQL of the migrations, as an artifact to review in the merge request |
| `scan:api` | every pipeline | Trivy; fails on fixable critical vulnerabilities |
| `migrate:staging` | `main`, `STAGING_ENABLED` | [`ci/migrate.sh`](ci/migrate.sh): runs this image's `migrate` as a one-off pod in staging's cluster, as the schema owner (Secret `database-credentials`) |
| `release:staging` | after `migrate:staging` | [`ci/release.sh`](ci/release.sh): commits `backend: api <sha>` (the api image's `newTag`) to the deploy repository's `main` |
| `migrate:production` | `main`, `PRODUCTION_ENABLED`, **manual** | the same against production |
| `release:production` | after `migrate:production` | pushes a branch to the deploy repository and opens the merge request "Release api `<sha>` to production"; merging it releases |

Migrations run before the release and must work with the version still running (expand/contract); a rollback is a
revert in the deploy repository and never touches the schema.

CI/CD variables:

| Variable | Scope | What |
| --- | --- | --- |
| `STAGING_ENABLED` | project or group | `"true"` creates `migrate:staging` and `release:staging` on `main` |
| `PRODUCTION_ENABLED` | project or group | `"true"` creates the production jobs (manual); needs `STAGING_ENABLED` too |
| `KUBE_CONTEXT` | staging, production | the cluster for the migrate jobs, through the GitLab agent: `<agent configuration project>:<agent name>` |
| `KUBECONFIG` | staging, production | File, instead of `KUBE_CONTEXT`: a kubeconfig whose current context is the cluster |
| `DEPLOY_REPO_TOKEN` | project; masked, protected | a project access token of the deploy repository with the role Maintainer and the scope `write_repository` |

The migrate jobs also store the zeiss group's deploy token (`gitlab-deploy-token`, `read_registry`, passed by GitLab
as `CI_DEPLOY_USER` / `CI_DEPLOY_PASSWORD`) in the cluster as the Secret `gitlab-registry`, to pull the image. No
database or Redis password is a variable: they live in the cluster (the deploy repository's `create-database.sh`
makes `database-credentials` once per environment). Until `STAGING_ENABLED` is set the pipeline stops, green, after
scan.
