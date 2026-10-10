# Product Management — API

The backend of the Product Management system: a .NET 10 / ASP.NET Core Web API with EF Core and PostgreSQL, a Redis
read cache, JWT authentication (tokens issued by the API to registered consumers), role-based authorization, rate
limiting and idempotency keys. This folder is self-contained, its own repository
([gitlab.com/zeiss/backend](https://gitlab.com/zeiss/backend)): the code, the tests, the image, the Kubernetes
manifests, the deploy script, the load tests and the GitLab pipeline.

The system as a whole (the web app, the gateway, the servers) is described in the [guide](../docs/README.md);
deployment and operations in [chapter 9](../docs/guide/09-deployment-and-operations.md).

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
`dotnet-tools.json` (`dotnet-ef`).

## Prerequisites

| Tool | For |
| --- | --- |
| .NET 10 SDK (10.0.401+, `global.json`) | building, tests, `dotnet run` |
| Docker | the tests (Testcontainers), the image, the migration step of the deploy script |
| `kubectl` with a cluster (Docker Desktop's Kubernetes, context `docker-desktop`) | deploying |
| `openssl`, `python3` | `deploy/local-secrets.sh` (local secrets), `scripts/check-requirements.py` |

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

What each suite proves: [chapter 10](../docs/guide/10-testing-and-quality.md).

## Run with `dotnet run`

```bash
deploy/local-secrets.sh                              # once: signing key and demo consumers → .NET user-secrets
dotnet run --project ProductManagement.Api           # http://localhost:5080 (Swagger: /swagger)
```

The API runs in the `Development` environment: `appsettings.Development.json` connects to PostgreSQL on
`localhost:5432` as the server's administrator (`product_management` / `product_management`), applies the
migrations and seeds the sample data on start-up; the token signing key and the demo consumers come from the .NET
user-secrets that `deploy/local-secrets.sh` wrote. Without a Redis connection string it uses in-memory replacements
(cache, idempotency keys, rate limits). A local database and Redis come from the infra repository
(`infra.sh up database redis`); to use that Redis:

```bash
ConnectionStrings__Redis=localhost:6379 Redis__Password=local-redis-password dotnet run --project ProductManagement.Api
```

Demo accounts: `viewer`/`viewer`, `clerk`/`clerk`, `manager`/`manager` (roles `viewer`, `stock-clerk`, `manager`).

## Deploy

[`deploy/deploy.sh`](deploy/deploy.sh) deploys the API to the current `kubectl` context from
`deploy/k8s/overlays/$ENVIRONMENT` (`local` by default):

```bash
deploy/deploy.sh                 # local: secrets (first run), image, migrations, rollout
```

Locally it:

1. creates the local secrets if they are missing ([`deploy/local-secrets.sh`](deploy/local-secrets.sh): a signing
   key, the demo consumers' hashes and `passwords.env` with the local database and Redis passwords, all in
   `deploy/k8s/overlays/local/generated/`, gitignored, readable only by you);
2. builds the image from this folder, tagged with the commit (plus `-dirty-<timestamp>` with uncommitted changes),
   which Docker Desktop's Kubernetes uses straight from the local Docker engine (any other context needs `API_IMAGE`,
   an image from a registry, and the script stops without it);
3. runs the database migrations with that image (`migrate`), as the schema owner `pm_migrator`, and seeds the sample
   data into an empty database;
4. renders the overlay with the image pinned (`deploy/k8s/.build/<environment>/`, not committed), applies it and
   waits for the rollout.

**What it expects from the other parts.** A PostgreSQL with the database `product_management` and the users
`pm_migrator` and `pm_app`, and a Redis, both reachable from the cluster and from Docker on this machine. Locally the
infra repository provides them (`infra.sh up database redis`, at `host.docker.internal`). The web app and the gateway
(frontend repository) reach the API through its Service; the API does not need them, but the load tests and the
requirements check go through the gateway.

Swagger (local and staging):
`kubectl -n product-management port-forward deployment/api 5080:8080` → http://localhost:5080/swagger.

### Variables

| Variable | Effect |
| --- | --- |
| `ENVIRONMENT` | `local` (default), `staging` or `production`: the overlay |
| `API_IMAGE` | an image already built and pushed (`registry/name:tag` or `name@sha256:…`): no local build |
| `MIGRATIONS=skip` | the migrations were already applied (the pipeline's migrate job) |
| `MIGRATION_CONNECTION_STRING` | the migrator's connection string, without password (default: the local database as `pm_migrator`) |
| `PM_MIGRATOR_PASSWORD` | the migrator's password (default: `local-migrator-password`) |
| `MIGRATION_REDIS` | Redis for the migration, which invalidates cached lists (default: `host.docker.internal:6379`) |
| `REDIS_PASSWORD` | Redis' password for the migration (default: `local-redis-password`) |
| `SEED_SAMPLE_DATA` | sample catalogue on an empty database (default `true`) |

## Configuration

* **Settings:** environment variables in [`deploy/k8s/base/deployment.yaml`](deploy/k8s/base/deployment.yaml)
  (ASP.NET Core reads `Section__Key` as `Section:Key`): token lifetime, rate limits, sign-in throttle, idempotency
  lifetimes, `Database__ApplyMigrationsOnStartup=false`, `OpenApi__Enabled=false`. Changing one changes the pod
  template, so applying it rolls the pods.
* **Per environment:** `deploy/k8s/overlays/<environment>/api.yaml`, a patch that sets only what differs, and always
  the two connection strings:

  | Environment | `ConnectionStrings__ProductManagement` | `ConnectionStrings__Redis` |
  | --- | --- | --- |
  | local | `Host=host.docker.internal;Port=5432;Database=product_management;Username=pm_app;Maximum Pool Size=10;GSS Encryption Mode=Disable` | `host.docker.internal:6379` |
  | staging | `Host=pm-db.staging.example.internal;…;Username=pm_app;Maximum Pool Size=10;SSL Mode=Require;…` | `pm-cache.staging.example.internal:6379` |
  | production | `Host=pm-db.example.internal;…;Username=pm_app;Maximum Pool Size=10;SSL Mode=Require;…` | `pm-cache.example.internal:6379` |

  Replace the `example.*` names with the real ones before the first staging or production deployment.
* **Secrets:** one Secret, `api-secrets`. No password is in any YAML file or connection string:

  | Key | Becomes | Locally | Staging, production |
  | --- | --- | --- | --- |
  | `database-password` | `PGPASSWORD` (Npgsql uses it when the connection string has no password) | `generated/passwords.env` | `PM_APP_PASSWORD` |
  | `redis-password` | `Redis__Password` (set on the Redis connection, wins over a password in the connection string) | `generated/passwords.env` | `REDIS_PASSWORD` |
  | `signing-key` | `Authentication__SigningKey` (ES256 private key, PEM) | `generated/signing-key.pem` | `API_SIGNING_KEY` (File) |
  | `consumers.json` | the file `/app/config/consumers.json` (client ids, PBKDF2 hashes, roles) | `generated/consumers.json` | `API_CONSUMERS` (File) |

  Locally the overlay's `secretGenerator` builds it as `api-secrets-<hash>`, so a changed file rolls the pods on the
  next deploy. `FORCE=1 deploy/local-secrets.sh` writes new files (and signs everyone out); with
  `PM_APP_PASSWORD` / `REDIS_PASSWORD` set it writes those passwords instead of the local defaults. In staging and
  production the pipeline creates the Secret from CI/CD variables
  ([`deploy/ci/create-secrets.sh`](deploy/ci/create-secrets.sh)); the overlays contain none.

**Another database or Redis:** edit the two connection strings in the overlay's `api.yaml`, put the passwords in
`generated/passwords.env` (locally), and tell the migration step where they are:

```bash
MIGRATION_CONNECTION_STRING="Host=db.example.internal;Port=5432;Database=product_management;Username=pm_migrator" \
PM_MIGRATOR_PASSWORD=... MIGRATION_REDIS=cache.example.internal:6379 REDIS_PASSWORD=... deploy/deploy.sh
```

Check the manifests as CI does (from this folder, or with Docker):

```bash
deploy/ci/validate-manifests.sh                     # needs kubectl, kubeconform and yq
docker run --rm -v "$PWD:/work:ro" -w /work alpine/k8s:1.34.11 deploy/ci/validate-manifests.sh
```

## Load tests and the requirements check

Both run against the deployed system, through the gateway (so the frontend must be deployed too).

```bash
load-tests/run.sh smoke                  # any of: smoke, load, stress, spike, stock-consistency, rate-limit, teams, browse
load-tests/seed-bulk.sh                  # 850,000 products and ~5 million stock movements (minutes)
load-tests/verify-data.sh                # data invariants, every count must be 0
python3 scripts/check-requirements.py    # the assessment's requirements: 14 checks against http://localhost:8080
```

`load-tests/run.sh` runs k6 as a Job inside the cluster. For every scenario except `rate-limit` it raises the API's
rate limits for the run (and the gateway's for stress, spike, browse and teams), saving the deployed values first and
setting them back afterwards. The data scripts reach PostgreSQL through `docker exec pm-postgres` by default; set
`PSQL` for another server. `check-requirements.py` takes `BASE_URL`, `CHECK_CLIENT_ID` and `CHECK_CLIENT_SECRET`.
Results: [load-test-results.md](../docs/load-test-results.md).

## CI/CD (GitLab)

[`.gitlab-ci.yml`](.gitlab-ci.yml): test (`test:backend`: format, build, every test suite with Docker-in-Docker;
`test:manifests`) → build (`build:api`; `migrations:sql`, the idempotent SQL for review) → scan (`scan:api`, Trivy)
→ staging (`migrate:staging` on the private runner, then `deploy:staging` through
[`deploy/ci/deploy-environment.sh`](deploy/ci/deploy-environment.sh)) → verify (`verify:requirements` against
`STAGING_URL`) → production (`migrate:production`, manual, then `deploy:production`).

CI/CD variables, scoped to the `staging` and `production` environments:

| Variable | Type | What |
| --- | --- | --- |
| `KUBE_CONTEXT` | Variable | `<agent configuration project path>:<agent name>` (the GitLab agent for Kubernetes) |
| `DB_SERVER`, `REDIS_SERVER` | Variable | the servers' private DNS names, for the migrate jobs |
| `PM_MIGRATOR_PASSWORD` | masked, protected | the schema owner, used only by the migrate jobs |
| `PM_APP_PASSWORD` | masked, protected | the API's database user → `api-secrets` |
| `REDIS_PASSWORD` | masked, protected | → `api-secrets` |
| `API_SIGNING_KEY` | File, protected | ES256 private key (`docker run --rm <api image> generate-signing-key`) → `api-secrets` |
| `API_CONSUMERS` | File, protected | `consumers.json` → `api-secrets` |
| `CHECK_CLIENT_ID`, `CHECK_CLIENT_SECRET` | staging only | a manager consumer for `verify:requirements` |

The three passwords are the same values the infra repository's pipeline sets on the servers: define them once as
group-level variables. The migrate jobs need a runner tagged `product-management-private` inside the private network
(the only place that can reach the database). Rotating a secret: change the variable and re-run the deploy job;
`create-secrets.sh` reports the Secret as changed and the job restarts the API. Full setup:
[chapter 12](../docs/guide/12-from-zero.md).
