# Product Management — API

The backend of the Product Management system: a .NET 10 / ASP.NET Core Web API with EF Core and PostgreSQL, a Redis
read cache, JWT authentication (tokens issued by the API to registered consumers), role-based authorization, rate
limiting and idempotency keys. This folder is self-contained, its own repository
([gitlab.com/zeiss/backend](https://gitlab.com/zeiss/backend)): the code, the tests, the image, the Kubernetes
manifests, the deploy script, the load tests and the GitLab pipeline.

The system as a whole (the web app, the gateway, the shared infrastructure) is described in the [guide](../docs/README.md);
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
| Docker | the tests (Testcontainers), the image |
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

The API runs in the `Development` environment: `appsettings.Development.json` connects to a PostgreSQL on
`localhost:5432` as `product_management` / `product_management` (any local PostgreSQL with that user and database),
applies the migrations and seeds the sample data on start-up; the token signing key and the demo consumers come from
the .NET user-secrets that `deploy/local-secrets.sh` wrote. Without a Redis connection string it uses in-memory
replacements (cache, idempotency keys, rate limits).

To run it against the cluster's shared PostgreSQL and Redis instead (namespace `infra`, reachable from this machine
only through a port-forward), as the schema owner, since Development applies the migrations:

```bash
kubectl -n infra port-forward svc/postgres 5432:5432 &
kubectl -n infra port-forward svc/redis 6379:6379 &
PGPASSWORD="$(kubectl -n product-management get secret database-credentials -o go-template='{{ index .data "migrator-password" | base64decode }}')" \
ConnectionStrings__ProductManagement="Host=localhost;Port=5432;Database=product_management;Username=pm_migrator" \
ConnectionStrings__Redis=localhost:6379 \
Redis__Password="$(kubectl -n infra get secret redis-auth -o go-template='{{ .data.password | base64decode }}')" \
dotnet run --project ProductManagement.Api
```

Demo accounts: `viewer`/`viewer`, `clerk`/`clerk`, `manager`/`manager` (roles `viewer`, `stock-clerk`, `manager`).

## Deploy

[`deploy/deploy.sh`](deploy/deploy.sh) deploys the API to the current `kubectl` context from
`deploy/k8s/overlays/$ENVIRONMENT` (`local` by default):

```bash
deploy/deploy.sh                 # local: secrets (first run), image, migrations, rollout
```

Locally it:

1. creates the API's own secrets if they are missing ([`deploy/local-secrets.sh`](deploy/local-secrets.sh): a
   signing key and the demo consumers' hashes, in `deploy/k8s/overlays/local/generated/`, gitignored, readable only
   by you; no passwords: those are in the cluster);
2. builds the image from this folder, tagged with the commit (plus `-dirty-<timestamp>` with uncommitted changes),
   which Docker Desktop's Kubernetes uses straight from the local Docker engine (any other context needs `API_IMAGE`,
   an image from a registry, and the script stops without it);
3. checks that the Secret `database-credentials` exists and copies the shared Redis' password from `infra/redis-auth`
   into the Secret `redis-credentials` (a pod reads only Secrets of its own namespace);
4. runs the database migrations with that image (`migrate`) **inside the cluster**, as a one-off pod
   (`kubectl run migrate-<tag> --rm --attach --restart=Never`, in `product-management`): it connects as the schema
   owner `pm_migrator` with the host, port, database, user and password from `database-credentials`, seeds the sample
   data into an empty database, streams its output, returns its exit code and leaves nothing behind. The database is
   reachable only inside the cluster, so no port-forward and no Job manifest are involved;
5. renders the overlay with the image pinned (`deploy/k8s/.build/<environment>/`, not committed), applies it and
   waits for the rollout.

**What it expects from the other parts.** The shared PostgreSQL and Redis in the namespace `infra`, and this
service's database: the infra repository's `infra.sh up`, then, once per environment,
`create-database.sh product-management product_management pm_app pm_migrator`, which creates the database, the users
`pm_migrator` (owns the schema) and `pm_app`, and the Secret `database-credentials` in `product-management`. What
`pm_app` may do on the tables is granted by this repository's own migration (`GrantAppPermissions`). The web app and
the gateway (frontend repository) reach the API through its Service; the API does not need them, but the load tests
and the requirements check go through the gateway.

Swagger (local and staging):
`kubectl -n product-management port-forward deployment/api 5080:8080` → http://localhost:5080/swagger.

### Variables

| Variable | Effect |
| --- | --- |
| `ENVIRONMENT` | `local` (default), `staging` or `production`: the overlay |
| `API_IMAGE` | an image already built and pushed (`registry/name:tag` or `name@sha256:…`): no local build |
| `MIGRATIONS` | `run` (default): migrate, then roll out; `skip`: already applied (the pipeline's deploy job); `only`: migrate, no rollout (the pipeline's migrate job) |
| `MIGRATION_REDIS` | Redis for the migration, which invalidates cached lists (default: `redis.infra.svc.cluster.local:6379`) |
| `SEED_SAMPLE_DATA` | sample catalogue on an empty database (default `true`) |

## Configuration

* **Settings:** environment variables in [`deploy/k8s/base/deployment.yaml`](deploy/k8s/base/deployment.yaml)
  (ASP.NET Core reads `Section__Key` as `Section:Key`): token lifetime, rate limits, sign-in throttle, idempotency
  lifetimes, `Database__ApplyMigrationsOnStartup=false`, `OpenApi__Enabled=false`. Changing one changes the pod
  template, so applying it rolls the pods.
* **Per environment:** `deploy/k8s/overlays/<environment>/api.yaml`, a patch that sets only what differs, and always
  the two connection strings:

  The same in every environment (local, staging, production): the cluster's shared PostgreSQL and Redis, in the
  namespace `infra`.

  | `ConnectionStrings__ProductManagement` | `ConnectionStrings__Redis` |
  | --- | --- |
  | `Host=postgres.infra.svc.cluster.local;Port=5432;Database=product_management;Username=pm_app;Maximum Pool Size=10;GSS Encryption Mode=Disable` | `redis.infra.svc.cluster.local:6379` |

* **Secrets:** no password is in any YAML file, connection string or CI/CD variable. The API reads three Secrets in
  its namespace:

  | Secret, key | Becomes | Made by |
  | --- | --- | --- |
  | `database-credentials`, `app-password` | `PGPASSWORD` (Npgsql uses it when the connection string has no password) | the infra repository's `create-database.sh` (random; also holds `host`, `port`, `database`, `app-user`, `migrator-user`, `migrator-password`, which only the migration pod reads) |
  | `redis-credentials`, `password` | `Redis__Password` (set on the Redis connection, wins over a password in the connection string) | `deploy/deploy.sh`, on every deploy: a copy of `infra/redis-auth` |
  | `api-secrets`, `signing-key` | `Authentication__SigningKey` (ES256 private key, PEM) | locally `generated/signing-key.pem`; staging, production `API_SIGNING_KEY` (File) |
  | `api-secrets`, `consumers.json` | the file `/app/config/consumers.json` (client ids, PBKDF2 hashes, roles) | locally `generated/consumers.json`; staging, production `API_CONSUMERS` (File) |

  Locally the overlay's `secretGenerator` builds `api-secrets` as `api-secrets-<hash>`, so a changed file rolls the
  pods on the next deploy. `FORCE=1 deploy/local-secrets.sh` writes a new key and hashes (and signs everyone out). In
  staging and production the pipeline creates `api-secrets` from CI/CD variables
  ([`deploy/ci/create-secrets.sh`](deploy/ci/create-secrets.sh)); the overlays contain none. New database passwords:
  `create-database.sh … --rotate`, then `kubectl -n product-management rollout restart deployment/api`.

**Another database or Redis** (a managed service, a server outside the cluster): put its host in the connection
string of the overlay's `api.yaml` (then with `SSL Mode=VerifyFull`, or `,ssl=true` for Redis). The migration pod
takes the database's host, port, name, users and passwords from `database-credentials`, so give the namespace that
Secret with the same keys; for Redis create `redis-credentials` (key `password`) by hand and set `MIGRATION_REDIS`
to its `host:port`: where the cluster has no `infra/redis-auth`, `deploy/deploy.sh` keeps that Secret as it is.

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
setting them back afterwards. The data scripts (`seed-bulk.sh`, `verify-data.sh`, and `run.sh` for its checks) reach
PostgreSQL inside the cluster by default
(`kubectl -n infra exec -i statefulset/postgres -- psql -U postgres -d product_management`), and `seed-bulk.sh` also
Redis (`kubectl -n infra exec -i deployment/redis -- redis-cli`); set `PSQL` / `REDIS_CLI` for other servers, or only
`PG_USER` / `PG_DATABASE`. `check-requirements.py` takes `BASE_URL`, `CHECK_CLIENT_ID` and `CHECK_CLIENT_SECRET`.
Results: [load-test-results.md](../docs/load-test-results.md).

## CI/CD (GitLab)

[`.gitlab-ci.yml`](.gitlab-ci.yml): test (`test:backend`: format, build, every test suite with Docker-in-Docker;
`test:manifests`) → build (`build:api`; `migrations:sql`, the idempotent SQL for review) → scan (`scan:api`, Trivy)
→ staging (`migrate:staging`, then `deploy:staging`, both through
[`deploy/ci/deploy-environment.sh`](deploy/ci/deploy-environment.sh) `migrate` / `deploy` in the `alpine/k8s` image:
the migrate job runs `deploy/deploy.sh` with `MIGRATIONS=only`, the deploy job with `MIGRATIONS=skip`) → verify (`verify:requirements` against
`STAGING_URL`) → production (`migrate:production`, manual, then `deploy:production`). The staging jobs exist once the
project or group variable `STAGING_ENABLED` is `true`, the production ones once `PRODUCTION_ENABLED` is `true` as well;
until then the pipeline stops, green, after scan. The deploy jobs need a deploy token named `gitlab-deploy-token`
(scope `read_registry`): they store it as the Secret `gitlab-registry-backend` the pods pull the image with.

CI/CD variables, scoped to the `staging` and `production` environments:

| Variable | Type | What |
| --- | --- | --- |
| `KUBE_CONTEXT` | Variable | `<agent configuration project path>:<agent name>` (the GitLab agent for Kubernetes) |
| `KUBECONFIG` | File | instead of `KUBE_CONTEXT`: a kubeconfig whose current context is the cluster (a self-hosted runner next to it) |
| `API_SIGNING_KEY` | File, protected | ES256 private key (`docker run --rm <api image> generate-signing-key`) → `api-secrets` |
| `API_CONSUMERS` | File, protected | `consumers.json` → `api-secrets` |
| `CHECK_CLIENT_ID`, `CHECK_CLIENT_SECRET` | staging only | a manager consumer for `verify:requirements` |

No database or Redis password is a CI/CD variable: they are random and live in the cluster
(`create-database.sh`, once per environment, before the first migrate job). Every job runs on gitlab.com's shared
runners: the migrate and deploy jobs reach the database only through the cluster (`kubectl`, with the GitLab agent or
a kubeconfig), so no runner has to be inside the private network. Rotating a key or consumer secret: change the
variable and re-run the deploy job; `create-secrets.sh` reports the Secret as changed and the job restarts the API. Full setup:
[chapter 12](../docs/guide/12-from-zero.md).
