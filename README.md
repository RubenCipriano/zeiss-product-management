# Product Management

A product and stock management system:

* **Backend:** .NET 10 / ASP.NET Core Web API, Entity Framework Core and PostgreSQL, with a Redis read cache, JWT
  authentication (tokens issued by the API to registered consumers), role-based authorization and rate limiting.
* **Frontend:** Angular 22 with Tailwind CSS, served behind an nginx gateway that also caches API reads.
* **Deployment:** the stateless parts (gateway, web app, API autoscaled 2–8 replicas) on Kubernetes, with Kustomize
  overlays for local, staging and production; PostgreSQL, Redis and the log servers (Grafana and Loki, optional) on
  their own servers outside the cluster, as in production (locally: Docker Compose containers). The system lives in
  three GitLab repositories, each with its own pipeline: **[backend](https://gitlab.com/zeiss/backend)** (the API and
  the documentation; this README), **[frontend](https://gitlab.com/zeiss/frontend)** (the web app and the gateway) and
  **[deploy](https://gitlab.com/zeiss/deploy)** (the servers outside the cluster and the logs). Commands here use them
  as the folders `backend/`, `frontend/` and `infra/` ([Repository layout](#repository-layout)).
* **At scale:** k6 load tests run inside the cluster, including ten teams on 850,000 products; the stock ledger
  measured at 20 million rows.
* **Stock ledger and idempotency:** every stock change is recorded with who made it; retried requests
  (`Idempotency-Key`) are applied exactly once, whichever replica they reach.
* **Observability:** the logs of every component, the browser's errors included, in one Loki, with Grafana
  dashboards and alerts.

![Product list](docs/images/product-list.png)

> **".NET Core":** since .NET 5 Microsoft dropped "Core" from the runtime's name. **.NET 10 is the current release
> of .NET Core** (cross-platform, not the Windows-only .NET Framework) and the current LTS. The web and data layers
> keep the name: **ASP.NET Core** and **Entity Framework Core**.

## Documentation

The full documentation is a guide in thirteen short chapters, each with an illustration, the source files to open and
the commands that prove what it says. Start at **[docs/README.md](docs/README.md)** (reading order: a 20-minute path
and a full path).

1. [Overview](docs/guide/01-overview.md): what the system does, who uses it, the pieces and the numbers
2. [The request journey](docs/guide/02-request-journey.md): from the browser to the database and back
3. [Caching](docs/guide/03-caching.md): the four cache layers and what invalidates them
4. [Security](docs/guide/04-security.md): sign-in, tokens, permissions, rate limits, browser and network
5. [Stock and consistency](docs/guide/05-stock-and-consistency.md): unique ids, no overselling, the ledger, exactly-once
6. [Scaling and capacity](docs/guide/06-scaling-and-capacity.md): autoscaling and what the load tests found
7. [Front end](docs/guide/07-frontend.md): Angular structure, data flow, accessibility, audits
8. [Logs, dashboards and alerts](docs/guide/08-observability.md): observability through logs, in Grafana
9. [Deployment and operations](docs/guide/09-deployment-and-operations.md): what runs where, the three parts and their deploy scripts, CI/CD, day 2
10. [Testing and quality](docs/guide/10-testing-and-quality.md): the test pyramid and what each layer proves
11. [Production readiness](docs/guide/11-production-readiness.md): what is done and what is left for go-live
12. [From zero to production](docs/guide/12-from-zero.md): a runbook for staging and production (servers, GitLab, TLS, backups)
13. [Reading the code](docs/guide/13-reading-the-code.md): where to look for what, one click file by file, and a glossary in plain words

## Contents

- [Documentation](#documentation)
- [Run it](#run-it) (on Kubernetes) and [without Kubernetes](#run-without-kubernetes)
- [Deploy each part](#deploy-each-part) (infra, backend, frontend on their own; without logs; staging and production)
- [What runs where](#what-runs-where)
- [Observability](#observability)
- [Develop](#develop)
- [Tests](#tests)
- [Requirements checklist](#requirements-checklist)
- [Architecture](#architecture) (diagrams and what each component does: [docs/architecture.md](docs/architecture.md))
- [API reference](#api-reference)
- [Security](#security)
- [Performance and scaling](#performance-and-scaling)
- [Key decisions](#key-decisions)
- [Assumptions](#assumptions)
- [Trade-offs and deviations from common practice](#trade-offs-and-deviations-from-common-practice)
- [Production readiness and next steps](#production-readiness-and-next-steps)
- [Repository layout](#repository-layout)

---

## Run it

Needs Docker and `kubectl`. The system is deployed in three parts, each with its own script, in this order:

| Part | Script | What it deploys |
| --- | --- | --- |
| [`infra/`](infra/README.md) | [`infra/infra.sh`](infra/infra.sh) | the servers outside the cluster (PostgreSQL, Redis, Loki + Grafana; locally Docker Compose containers) and the cluster's log collector |
| [`backend/`](backend/README.md) | [`backend/deploy/deploy.sh`](backend/deploy/deploy.sh) | the API: image, database migrations, rollout |
| [`frontend/`](frontend/README.md) | [`frontend/deploy/deploy.sh`](frontend/deploy/deploy.sh) | the web app and the gateway: images, rollout |

Each script deploys to the current `kubectl` context. All commands below run from a folder that holds the three
repositories cloned as `backend/`, `frontend/` and `infra/` ([Repository layout](#repository-layout)).

### On Docker Desktop's Kubernetes (recommended; everything visible in Docker Desktop)

1. Docker Desktop → **Settings → Kubernetes → Enable Kubernetes** → Apply & restart (single node).
2. Deploy:

   ```bash
   kubectl config use-context docker-desktop
   infra/cluster-addons.sh        # once per cluster: metrics-server, which the autoscalers need
   infra/infra.sh up              # PostgreSQL, Redis, Loki + Grafana + agents (and the cluster's log collector)
   backend/deploy/deploy.sh       # secrets (first run), API image, migrations, rollout
   frontend/deploy/deploy.sh      # web + gateway images, rollout → http://localhost:8080
   ```

   In order:
   * **infra:** starts the servers as containers on this machine, one Docker Compose file per component:
     PostgreSQL 18 (`infra/database.yaml`, which also creates the least-privilege database users `pm_migrator` and
     `pm_app`), Redis 8 (`infra/redis.yaml`), and Loki, Grafana and the agent that ships the servers' logs
     (`infra/logs.yaml`). It then applies the cluster's log collector (Alloy, namespace `observability`);
   * **backend, local secrets (first run only):** `backend/deploy/local-secrets.sh` generates this machine's token
     signing key, the demo consumers' secret hashes and a file with the local database and Redis passwords (never
     committed);
   * **backend:** builds `backend/` into an image with an immutable tag, **runs the database migrations with that image
     as the schema owner `pm_migrator`** (and seeds sample data into an empty database), then rolls out the API;
   * **frontend:** builds the Angular app and the gateway images and rolls them out.

3. Open **http://localhost:8080** and sign in (below).

What you get:

| What | Where |
| --- | --- |
| Application | http://localhost:8080 (the gateway is a `LoadBalancer` Service on localhost) |
| Grafana: logs, dashboards, alerts | http://localhost:3000 (no login locally; dashboards in the folder *Product Management*) |
| Loki | http://localhost:3100 |
| Swagger UI | `kubectl -n product-management port-forward deployment/api 5080:8080` → http://localhost:5080/swagger (*Authorize* with a token) |
| PostgreSQL | `localhost:5432`, database `product_management`; administrator `product_management` / `product_management` (the API uses `pm_app`) |
| Redis | `localhost:6379`, password `local-redis-password` |

After changing the API, run `backend/deploy/deploy.sh` again; after changing the web app or the gateway,
`frontend/deploy/deploy.sh`. Each part also deploys on its own, without Grafana, or against other servers:
[Deploy each part](#deploy-each-part). Staging and production use the same scripts from each part's GitLab pipeline
([chapter 9](docs/guide/09-deployment-and-operations.md)).

Stop the servers with `infra/infra.sh down` (data volumes are kept); delete the application with
`kubectl delete namespace product-management` (`infra/infra.sh down logs` removes the `observability` namespace).

### Sign in

The API issues its own JWTs to **registered API consumers** (client id + secret). The web app's login page and the
load tests both use `POST /api/auth/token`. Local demo consumers:

| Client id / user | Secret      | Role          | Can                                    |
| ---------------- | ----------- | ------------- | -------------------------------------- |
| `viewer`         | `viewer`    | `viewer`      | browse and search                      |
| `clerk`          | `clerk`     | `stock-clerk` | + add and remove stock                 |
| `manager`        | `manager`   | `manager`     | + create, edit and delete products     |

Only their PBKDF2 hashes are configured, and neither the hashes nor the token signing key are committed:
[`backend/deploy/local-secrets.sh`](backend/deploy/local-secrets.sh) generates them on the first
`backend/deploy/deploy.sh` (a new key per machine) into `backend/deploy/k8s/overlays/local/generated/` (gitignored;
the Secret `api-secrets`) and into the API's .NET user-secrets for `dotnet run`.

```bash
# Get a token and call the API from a terminal
TOKEN=$(curl -s -X POST localhost:8080/api/auth/token -H 'Content-Type: application/json' \
  -d '{"clientId":"manager","clientSecret":"manager"}' | jq -r .accessToken)
curl -H "Authorization: Bearer $TOKEN" localhost:8080/api/products
```

### Run without Kubernetes

For working on the code: the API and the Angular dev server run on your machine, against the same database and
cache servers. Needs the .NET 10 SDK and Node.js 24 ([Develop](#develop)).

```bash
infra/infra.sh up database redis                                # the database and cache servers (containers)

backend/deploy/local-secrets.sh                                 # once: signing key and demo consumers → user-secrets
dotnet run --project backend/ProductManagement.Api              # http://localhost:5080 (Swagger: /swagger)
cd frontend && npm ci && npm start                              # http://localhost:4200, /api proxied to :5080
```

The API runs in the `Development` environment (`appsettings.Development.json`, plus the signing key and the demo
consumers from the .NET user-secrets that `backend/deploy/local-secrets.sh` wrote): it connects to `localhost:5432`
as the server's administrator (`product_management`), applies the migrations and seeds the sample data on start-up,
and uses in-memory replacements for Redis (cache, idempotency keys, rate limits). To use the Redis server as the
cluster does, start it with the connection string and the password:

```bash
ConnectionStrings__Redis="localhost:6379" Redis__Password=local-redis-password dotnet run --project backend/ProductManagement.Api
```

Check it: `curl localhost:5080/health/ready` answers `Healthy`, and the token command above works against
`localhost:5080` (or `localhost:4200`, through the dev server's proxy). The demo accounts are the same. Tables created
this way belong to the administrator; run `infra/infra.sh up database` again before a later
`backend/deploy/deploy.sh`: its `roles` step hands them to `pm_migrator`.

---

## Deploy each part

The three parts only meet at a few addresses, so each one deploys on its own. The commands below use the folder
names (`infra/infra.sh`, `backend/deploy/deploy.sh`, `frontend/deploy/deploy.sh`); in a separate clone of one
repository, run its command from that repository's root (`./infra.sh up`, `./deploy/deploy.sh`).

* the **backend** needs a PostgreSQL with the users `pm_migrator` and `pm_app` and a Redis
  ([`infra/postgres/roles.sql`](infra/postgres/roles.sql)); it finds them through the two connection strings in its
  overlay;
* the **frontend** needs the API (the gateway's `API_UPSTREAM` and `JWKS_URL`) and, optionally, the log collector
  (`COLLECTOR_UPSTREAM`);
* the **infra** needs nothing from the others.

### Prerequisites

| Tool | For |
| --- | --- |
| Docker Desktop with Kubernetes enabled, `kubectl` context `docker-desktop` | everything (the deploy scripts use the images they build straight from the local Docker engine; any other cluster takes registry images: `API_IMAGE`, `WEB_IMAGE`, `GATEWAY_IMAGE`) |
| `openssl` and `python3` | `backend/deploy/local-secrets.sh` (the local signing key, consumers and passwords); `python3` also for the requirements check |
| .NET 10 SDK | only for `dotnet run` and the backend tests (the deploy script builds in Docker) |
| Node.js 24 | only for the frontend's dev server, unit tests and Playwright (the deploy script builds in Docker) |

### Everything, locally

```bash
infra/infra.sh up                 # PostgreSQL, Redis, Loki + Grafana + agents (and the cluster's log collector)
backend/deploy/deploy.sh          # secrets (first run), API image, migrations, rollout
frontend/deploy/deploy.sh         # web + gateway images, rollout → http://localhost:8080
```

### Infra, by component

`infra/infra.sh up|down|status [database] [redis] [logs]`; no component means all three
([infra/README.md](infra/README.md)).

```bash
infra/infra.sh up database redis  # only PostgreSQL and Redis (no Loki, no Grafana)
infra/infra.sh up logs            # Loki, Grafana, the server agent, and the cluster's log collector
infra/infra.sh down logs          # remove them again (the in-cluster collector too); data volumes are kept
infra/infra.sh status             # the server containers and the cluster's collector
```

Passwords are local development defaults (`POSTGRES_PASSWORD`, `PM_MIGRATOR_PASSWORD`, `PM_APP_PASSWORD`,
`REDIS_PASSWORD`); set them in the environment or in `infra/.env` (gitignored) to use others, and give the backend
the same values (below). The data volumes (`pm-postgres-data`, `pm-loki-data`, `pm-grafana-data`) are created by
`infra.sh` as external volumes, so neither `down` nor `docker compose down -v` deletes them.

### Backend only

With the local infra running (`infra/infra.sh up database redis`):

```bash
backend/deploy/deploy.sh          # secrets (first run), API image, migrations, rollout
```

With another database or Redis (another machine, a managed service):

1. Edit the two connection strings in `backend/deploy/k8s/overlays/<environment>/api.yaml` (`local` here):
   `ConnectionStrings__ProductManagement` (host, port, database, user `pm_app`, no password) and
   `ConnectionStrings__Redis` (`host:port`, no password).
2. Set the API's passwords. Locally they are in `backend/deploy/k8s/overlays/local/generated/passwords.env`
   (`database-password=`, `redis-password=`): edit the file, or write it again from the environment:

   ```bash
   FORCE=1 PM_APP_PASSWORD=... REDIS_PASSWORD=... backend/deploy/local-secrets.sh
   ```

   `FORCE=1` also generates a new signing key and new consumer hashes, which signs everyone out. In staging and
   production the passwords come from GitLab variables (below).
3. Deploy, telling the migration step where the database and Redis are and the migrator's password (the migration
   runs in a container on this machine, so those hosts must be reachable from Docker):

   ```bash
   MIGRATION_CONNECTION_STRING="Host=db.example.internal;Port=5432;Database=product_management;Username=pm_migrator" \
   PM_MIGRATOR_PASSWORD=... MIGRATION_REDIS=cache.example.internal:6379 REDIS_PASSWORD=... \
   backend/deploy/deploy.sh
   ```

More in [backend/README.md](backend/README.md).

### Frontend only

```bash
frontend/deploy/deploy.sh         # web + gateway images, rollout → http://localhost:8080
```

The gateway finds the API through `API_UPSTREAM` (`host:port`) and `JWKS_URL` (the API's public keys) in
`frontend/deploy/k8s/overlays/<environment>/gateway.yaml`. They point at the API deployed by the backend in the same
cluster (`api.product-management.svc.cluster.local:8080`); to use an API elsewhere, put its address in both and
deploy again. More in [frontend/README.md](frontend/README.md).

### Without Grafana (no logs)

```bash
infra/infra.sh up database redis
backend/deploy/deploy.sh
LOGS=off frontend/deploy/deploy.sh
```

The application works the same. The pods still log to stdout (`kubectl -n product-management logs deployment/api`);
`LOGS=off` sets the gateway's `COLLECTOR_UPSTREAM` to `""`, so `/collect` answers 204 and drops the browser's error
reports instead of failing. The Playwright test that reads browser errors back from Loki is skipped when no Loki
answers. Turn the logs on later with `infra/infra.sh up logs` and `frontend/deploy/deploy.sh`.

### With another Grafana / Loki

To ship the logs to another Loki (e.g. Grafana Cloud) instead of the local containers:

```bash
LOKI_URL=https://<host>/loki/api/v1/push LOKI_USERNAME=<user> LOKI_PASSWORD=<token> infra/infra.sh up logs
```

No Loki or Grafana container starts; the server agent and the cluster's collector push to `LOKI_URL` (the
credentials go into the Secret `loki-credentials` in the `observability` namespace). The dashboards and alert rules
in `infra/logs/grafana/` can be imported into that Grafana.

### Staging and production (GitLab)

Each part is its own GitLab project with its own pipeline (`backend/.gitlab-ci.yml`, `frontend/.gitlab-ci.yml`,
`infra/.gitlab-ci.yml`). Their CI/CD variables, scoped to the `staging` and `production` environments:

| Project | Variables |
| --- | --- |
| infra | `KUBE_CONTEXT`, `CLUSTER_LOKI_URL`, `LOKI_USERNAME` / `LOKI_PASSWORD` (hosted Loki only), `POSTGRES_PASSWORD`, `PM_MIGRATOR_PASSWORD`, `PM_APP_PASSWORD`, `REDIS_PASSWORD` |
| backend | `KUBE_CONTEXT`, `DB_SERVER`, `REDIS_SERVER`, `PM_MIGRATOR_PASSWORD`, `PM_APP_PASSWORD`, `REDIS_PASSWORD`, `API_SIGNING_KEY` (File), `API_CONSUMERS` (File); staging only: `CHECK_CLIENT_ID`, `CHECK_CLIENT_SECRET` |
| frontend | `KUBE_CONTEXT`; staging only: `E2E_VIEWER_SECRET`, `E2E_CLERK_SECRET`, `E2E_MANAGER_SECRET` |

`PM_MIGRATOR_PASSWORD`, `PM_APP_PASSWORD` and `REDIS_PASSWORD` must be the same in infra and backend: define them
once as group-level variables. Deploy in the same order: infra (manual jobs, per component), then the backend
pipeline (migrate, deploy, verify, manual production), then the frontend pipeline. The full setup (runners, the
GitLab agent, environments) is [chapter 12](docs/guide/12-from-zero.md).

### Swagger, Grafana and the demo accounts

* Swagger (local and staging): `kubectl -n product-management port-forward deployment/api 5080:8080` →
  http://localhost:5080/swagger.
* Grafana: http://localhost:3000, dashboards in the folder *Product Management*; Loki at http://localhost:3100.
* Demo accounts: `viewer`/`viewer`, `clerk`/`clerk`, `manager`/`manager` (roles `viewer`, `stock-clerk`, `manager`).

---

## What runs where

![What runs where: the cluster with the gateway, web, API and Alloy; outside it the CI/CD pipeline, the database, cache and log servers and the server agent](docs/images/kubernetes-map.svg)

| Where | What | Part | Locally |
| --- | --- | --- | --- |
| Cluster, namespace `product-management` | **gateway** (nginx, 2–6 pods, HPA): the only way in; per-IP rate limit, token check, 1 s microcache per role, routing | frontend | Service `gateway`, `LoadBalancer` on localhost:8080 (staging/production: an Ingress with TLS) |
| | **web** (nginx, 2 pods): the Angular files | frontend | |
| | **api** (ASP.NET Core, 2–8 pods, HPA): its only Service, `api`, is headless, so the gateway balances per request and new pods get traffic at once. Its configuration is plain environment variables in the Deployment; the connection strings name the database and Redis servers directly | backend | connection strings to `host.docker.internal` |
| | **Secret `api-secrets`**: the database and Redis passwords, the token signing key, the registered consumers | backend | generated by `backend/deploy/local-secrets.sh` |
| Cluster, namespace `observability` | **alloy**: reads every pod's log and pushes it to Loki (`LOKI_URL`); also receives the browser's error reports (`faro.receiver`, reachable only from the gateway's `/collect`). Optional | infra | Loki at `host.docker.internal:3100` |
| Cluster, `kube-system` | **metrics-server**: CPU per pod for the autoscalers | infra | `infra/cluster-addons.sh` |
| Database server | **PostgreSQL 18**: users `pm_migrator` (owns the schema, runs migrations) and `pm_app` (the API: rows only, 30 s statement timeout) | infra | container `pm-postgres`, port 5432 (`infra/database.yaml`) |
| Cache server | **Redis 8**: cache, idempotency keys, rate limits, failed sign-ins; memory only, password | infra | container `pm-redis`, port 6379 (`infra/redis.yaml`) |
| Log server (optional) | **Loki** (stores the logs, 14 days) and **Grafana** (two dashboards, 12 alert rules) | infra | containers `pm-loki` (3100, volume `pm-loki-data`), `pm-grafana` (3000) (`infra/logs.yaml`) |
| On the servers | **server agent** (Alloy): ships PostgreSQL's and Redis' logs to Loki | infra | container `pm-server-agent` (`infra/logs.yaml`) |
| CI/CD runner | **migrations**: the API image run with `migrate` as `pm_migrator`, before every rollout | backend | `backend/deploy/deploy.sh` does it with `docker run` |

Every object, Secret and policy: [docs/architecture.md](docs/architecture.md). Every flow (sign-in, token check at the
gateway, the cache layers, stock changes with idempotency, rate limiting, deployment, CI/CD, logs, autoscaling, load
tests) as a diagram: [docs/flows.md](docs/flows.md). Why the servers are outside the cluster and how the overlays and
the three parts work: [ADR 0021](docs/adr/0021-deployment-layout.md).

The API scales on its own (HorizontalPodAutoscaler, 2–8 replicas at 60 % CPU; the gateway 2–6 at 70 %). Change its
range without touching the frontend:

```bash
kubectl -n product-management patch hpa api -p '{"spec":{"minReplicas":4}}'
kubectl -n product-management get hpa api -w
```

Large dataset for scale testing: `backend/load-tests/seed-bulk.sh` (850,000 products, 5 million stock movements, ~3–6
minutes; `PRODUCTS=0 LEDGER_ROWS=20000000 backend/load-tests/seed-bulk.sh` grows the ledger to 20 million rows, ~20
minutes), checked by `backend/load-tests/verify-data.sh`.

---

## Observability

Logs only, in one place: **Loki** on the log server, kept for **14 days** (locally in the Docker volume
`pm-loki-data`). What goes there:

| Source | How it gets to Loki | Query |
| --- | --- | --- |
| **Browser** (the Angular app): errors only | Grafana Faro SDK in the page → gateway `POST /collect` (same origin) → Alloy's `faro.receiver` in the cluster | `{namespace="browser"}` |
| **Gateway and web** (nginx) and the **API** | they write JSON to stdout; Alloy in the cluster reads every pod's log | `{namespace="product-management", app="gateway"}` (or `"web"`, `"api"`) |
| **Servers**: PostgreSQL, Redis, Loki, Grafana | the server agent (Alloy) reads the containers' logs | `{namespace="servers", app="postgres"}` (or `"redis"`, …) |

**Grafana** (http://localhost:3000) has two dashboards: **Product Management** (traffic, errors, latency and
microcache from the gateway's access log, the API's warnings and errors, PostgreSQL's slow statements and lock waits,
Redis) and **Logs — where everything goes** (the raw lines of the frontend, backend and servers side by side), plus
**12 LogQL alert rules**, each with a runbook. The gateway gives every request a W3C trace id and logs it; the API's
JSON log lines carry the same id, so one id finds every line of a request. Browser reports carry the error, the route
without its query string, the release, the browser and the user id; never the token, form values or a session id.

Why logs only, why on its own server, what each browser report contains, the runbooks and what would come next
(traces and metrics): [chapter 8](docs/guide/08-observability.md).

**The logs are optional.** Loki, Grafana and the collectors are the infra's `logs` component: without it
(`infra/infra.sh up database redis`, and the frontend deployed with `LOGS=off frontend/deploy/deploy.sh`) the
application works the same, the pods log to stdout (`kubectl logs`), and the gateway answers the browser's error
reports with 204 and drops them. To use another Loki (e.g. Grafana Cloud), start the logs with `LOKI_URL`:
[Deploy each part](#with-another-grafana--loki).

---

## Develop

| Tool      | Version |
| --------- | ------- |
| .NET SDK  | 10.0.401+ (pinned in `backend/global.json`) |
| Node.js   | 24 LTS (`frontend/.nvmrc`; 22.22+ also supported) |
| Docker    | the servers, the images, and the integration tests (Testcontainers) |
| kubectl   | for deployment (Kustomize is built in) |

**Backend:** open `backend/ProductManagement.sln` in **Visual Studio 2026** (the release that supports .NET 10) or
JetBrains Rider. Projects sit directly under `backend/`, grouped by the `Source` and `Tests` solution folders.
`ProductManagement.Api.http` has a request for every endpoint. Run the API from the IDE (profile `http`, port 5080)
against the servers, as in [Run without Kubernetes](#run-without-kubernetes).

**Frontend:**

```bash
cd frontend && npm ci && npm start    # http://localhost:4200; /api proxied to :5080
```

| Script                 | Purpose |
| ---------------------- | ------- |
| `npm start`            | dev server with live reload |
| `npm test` / `test:ci` | Vitest (watch / single run) |
| `npm run lint`         | ESLint (angular-eslint) |
| `npm run format:check` | Prettier |
| `npm run build`        | production build |

---

## Tests

```bash
cd backend && dotnet test --solution ProductManagement.sln     # 381 tests (Docker must be running)
cd frontend && npm run test:ci                                   # 185 tests
cd frontend && npm run e2e                                       # 22 Playwright tests against the running system
python3 backend/scripts/check-requirements.py                    # the assessment PDF, checked against the running system
backend/load-tests/run.sh smoke                                  # against the running cluster
```

| Suite                                 | Tests | What it proves |
| ------------------------------------- | ----: | -------------- |
| `UnitTests`, folder `Core/`           | 116 | **Domain:** invariants (six-digit ids, price precision, stock range); a failed update leaves no partial changes; the stock rule at its edges, including deltas that would overflow naive arithmetic. **Use cases:** validation first, no id consumed by invalid requests, version checks, conflicts mapped to 409/412, stock errors carry the available quantity, an exhausted id sequence becomes `product.ids_exhausted`; validators at their boundaries. |
| `UnitTests`, folder `Infrastructure/` | 31 | Registration (refuses to start without Redis outside development, in-memory fallbacks in development, invalid options rejected), cache invalidation only after a successful commit, single-flight, the caching decorator, a cancelled or abandoned Redis command treated like a Redis outage. |
| `UnitTests`, folder `Api/`            | 65 | Token service (blocked callers refused before checking credentials), PBKDF2 secret hashing, authentication options validation, idempotency key rules and request fingerprints, the shared token-bucket rate limiter, `If-Match` and `If-None-Match` parsing. |
| `Api.IntegrationTests`                | 153 | Real HTTP against **real PostgreSQL and Redis** (Testcontainers). Every endpoint and the problem-details contract. **Concurrency:** 60 parallel orders on 25 units → exactly 25 succeed; no lost updates; two API instances never produce a duplicate id. **Ledger:** every kind of change recorded, ledger sum = stock under concurrency. **Idempotency:** 20 simultaneous copies over two replicas run once; a retry on the other replica is replayed; a retry after Redis lost the claim is still not applied twice (database guard), for stock changes and creates; reused or invalid keys refused, also when Redis has forgotten the key (`422` from the ledger); no Redis → refused rather than risk a duplicate. **Ids:** an exhausted sequence answers `503 product.ids_exhausted`. **Conditional GET:** `If-None-Match` with the current `ETag` → `304`. **Readiness:** stays ready while the request pool is saturated. **Cache:** hits, invalidation visible across replicas, lists kept across stock changes yet showing current stock, 404s not cached, works with Redis down. **Security:** token endpoint (valid, invalid, missing, throttled, failed attempts adding up across two replicas); 30-case role × operation matrix; missing, expired, foreign and forged tokens, `alg: none`, HS256 signed with the public key; the published JWKS has no private part and matches the tokens' `kid`; anonymous probes. **Rate limiting:** per user, shared by two replicas, stricter for writes, `Retry-After`. |
| `Api.AcceptanceTests` (BDD, Reqnroll) | 16 | Gherkin scenarios from the user's point of view (`Features/*.feature`): create, browse with stock, search, low stock, update, delete, conflicting edits, invalid details naming each problem, stock in and out, no overselling, unknown products → not found. |
| Frontend (Vitest)                     | 185 | API client contract (URLs, `If-Match`, `Idempotency-Key`, cache bypass after own changes), **browser cache** (fresh hit without a request, stale-while-revalidate, bypass, cleared after writes, LRU eviction), store (next-page prefetch, page change vs background refresh), pipes, progress bar, stock history dialog, a retried stock change reuses its key, login opened without `returnUrl`, error normalisation, store (no duplicate requests, no flicker, background refresh paused in hidden tabs), auth (login against the token endpoint, session restore and expiry, role → permission mapping, guard, interceptor never leaks the token to other origins), login page (safe `returnUrl`, 401/429 messages), dialogs (validation, server field errors, 412 conflict + reload of the latest version past the gateway's microcache), role-specific UI, paginator, toasts, the product table's caption for screen readers, **browser error reporting** (errors only, queued until the SDK loads, no query string or token sent, never throws, off in dev mode). 34 spec files. |
| End-to-end (Playwright)               | 22 | 11 scenarios on a desktop and a phone viewport against the running system: sign-in per role, the manager's full journey, paging served by the browser cache, security headers, `401` at the gateway, missing files are `404`, a real error thrown in the page read back from Loki (skipped when no Loki is reachable, e.g. with `LOGS=off`); any CSP violation or uncaught error fails the test. |
| Load (k6, in cluster)                 | 8 scenarios | smoke, load (SLO thresholds), stress, spike, **stock consistency across replicas**, rate limiting, **teams** (many teams on 850,000 products, 30 % duplicate retries, then ledger = applied changes; `PEAK=` turns it into a capacity staircase), **browse** (read capacity with a hot set). See the [results](docs/load-test-results.md). |

The GitLab pipelines run the same scripts, one per repository:

* `backend/.gitlab-ci.yml`: test (build with warnings as errors, `dotnet format --verify-no-changes`, unit,
  integration and acceptance tests, manifests) → build (API image, migration SQL) → scan (Trivy) → staging (migrate,
  then deploy) → verify (the PDF check against staging) → production (manual migrate, then deploy);
* `frontend/.gitlab-ci.yml`: test (ESLint and Prettier, unit, build, manifests) → build (web and gateway images) →
  scan → staging (deploy) → verify (Playwright against staging) → production (manual deploy);
* `infra/.gitlab-ci.yml`: validate (Compose files, the log collector's manifests, Alloy configuration) → manual
  deploy jobs per environment and component.

The manifests step is each part's `deploy/ci/validate-manifests.sh` (kubeconform, the settings each overlay must set,
no Secret where there must be none). The pipelines are **not yet run on a GitLab instance**: each file is validated
against GitLab's CI schema and its job graph checked with `npx gitlab-ci-local@4 --list` from inside the part. The
setup is [chapter 12](docs/guide/12-from-zero.md).

What each kind of test proves, with examples: [docs/guide/10-testing-and-quality.md](docs/guide/10-testing-and-quality.md).

---

## Requirements checklist

Every row below is also **checked against the running system** by `backend/scripts/check-requirements.py` (14 checks
through the gateway: the 9 endpoints, add and decrement by any quantity, partial search, stock range, 40 concurrent
creates over the replicas → 40 unique 6-digit ids, validation, stock in every list).

| Requirement (assessment PDF)                                | Where / how |
| ----------------------------------------------------------- | ----------- |
| CRUD endpoints                                              | `ProductsController`: `GET/POST /api/products`, `GET/PUT/DELETE /api/products/{id}` |
| `POST /api/products/{id}/decrement-stock/{quantity}` → OK    | One atomic conditional UPDATE; 200 + updated product ([ADR 0003](docs/adr/0003-atomic-stock-updates.md)) |
| `POST /api/products/{id}/add-to-stock/{quantity}` → OK       | Same mechanism, upper bound enforced. Any quantity: in the UI the + / − buttons move 1, 5, 10, 25 or 100 units per click, and *Adjust stock* takes any amount |
| `GET /api/products/search?name=` (partial match)            | Case-insensitive `ILIKE`, wildcards escaped, trigram index |
| `GET /api/products/stock-level?min=&max=`                   | Inclusive range, either bound optional |
| Unique 6-digit id, no duplicates across instances           | PostgreSQL sequence `100000–999999 NO CYCLE` ([ADR 0002](docs/adr/0002-product-id-generation.md)); tested with two API hosts, with 40 concurrent creates over the replicas (`check-requirements.py`) and on 850,000 products (`verify-data.sh`) |
| Validation of create/update                                 | FluentValidation + domain guards + database check constraints ([ADR 0005](docs/adr/0005-error-handling.md)) |
| Stock included in every list response                       | `stockQuantity` on every `ProductResponse` |
| EF Core code-first migrations                               | `backend/ProductManagement.Infrastructure/Persistence/Migrations` (generated by `dotnet ef`), applied once per deployment, before the rollout, by the API image's `migrate` command as the schema owner (`backend/deploy/deploy.sh` locally, a pipeline job in CI/CD) |
| Database seeding                                            | Categories via `HasData`; sample products via `UseSeeding`/`UseAsyncSeeding` |
| Angular frontend; reflects changes made through the API     | `frontend/`: every endpoint used; refetch after changes + background refresh ([ADR 0007](docs/adr/0007-frontend-state.md)) |
| Unit tests for various scenarios                            | 212 backend unit tests + 185 frontend tests (plus 153 integration tests, 16 BDD scenarios and 22 end-to-end tests) |
| *Optional:* BDD tests                                       | `ProductManagement.Api.AcceptanceTests` (Reqnroll / Gherkin) |
| Documentation for running locally                           | This README and [`docs/`](docs) |

---

## Architecture

### Backend (clean architecture)

```
ProductManagement.Api ──────────────────────────────► ProductManagement.Core
        │                                                     ▲
        └──► ProductManagement.Infrastructure ────────────────┘
                     └──► EF Core / PostgreSQL, Redis
```

| Project          | Responsibility |
| ---------------- | -------------- |
| `Core`           | The domain and the use cases, organised by feature (`Products/`, `Categories/`, `Common/`). **Domain:** `Product`, `Category`, invariants, `ProductRules` (single source of limits), the stock rule (`StockAdjustment`: the change, its ledger entry and the range the current stock must be in), `StockMovement`. **Use cases:** `ProductQueryService`, `ProductCommandService`, `StockCommandService`, contracts, FluentValidation, `Result`/`Error`, ports (`IProductRepository`, `IProductQueries`, `IProductIdGenerator`, `IUnitOfWork`). No EF Core, no ASP.NET Core: the compiler keeps it that way, because the project does not reference them. |
| `Infrastructure` | `DbContext`, configurations, migrations, repositories and queries (stock changes as one SQL statement), sequence-based id generator, **Redis cache decorator**, seeding, health checks. |
| `Api`            | Thin controllers, HTTP mapping (status codes, ETag/If-Match, problem details), **token endpoint, JWT verification, authorization policies, rate limiting**, OpenAPI, health endpoints, the `migrate` and `hash-secret` commands. |

A request end to end (`PUT /api/products/{id}`):

1. **Gateway:** checks the per-IP limit and picks the least busy API replica (reads may be answered from its
   1 s microcache).
2. **API pipeline:** validates the JWT, applies the per-user limit (stricter for writes) and checks the
   `products:write` permission.
3. **Controller:** parses `If-Match` and calls the use case. It has no other logic.
4. **Use case:** validates the request, loads the aggregate, compares versions (412 if stale), checks the category
   and calls `product.Update(...)`. The **domain** enforces the invariants.
5. **Unit of work:** saves. EF Core puts the loaded `xmin` in the `WHERE`, so a concurrent change becomes 412 or
   409. After the commit, the **cache generation** is incremented and every replica stops serving the old data.
6. **Response:** the query side re-reads the product, and the controller returns it with a fresh `ETag`.

**EF Core:**
* reads are no-tracking projections;
* writes load tracked aggregates only for load-modify-save;
* a stock change is one raw SQL statement in `ProductRepository`: a data-modifying CTE with the conditional
  `UPDATE` and the ledger `INSERT`, so they succeed or fail together;
* everything is async and honours cancellation, with `DbContext` pooling;
* the database repeats the domain constraints;
* migrations run once per deployment, before the rollout, with the API image's `migrate` command as `pm_migrator`;
  the API pods never migrate (`ApplyMigrationsOnStartup=false`) and their database user cannot change the schema.

### Frontend

```
src/app/
  core/       auth (session, guard, interceptor), http (ApiError + interceptor), config, notifications,
              observability (browser errors to Loki: ReportingErrorHandler + Grafana Faro, loaded lazily)
  shared/     forms (validators, messages), models, pipes, util, ui (dialog, confirm dialog, icon, paginator,
              progress bar, toasts)
  features/auth/login/  sign-in page
  features/products/
    data-access/  ProductsApi, CategoriesApi, ProductsStore (signals + rxResource), models
    feature/      products page, ProductActions, form, stock-adjust and stock-history dialogs
    ui/           presentational components: inventory summary, filters, product table, stock status, pipes
```

`data-access` talks to the API, `feature` wires pages and user actions, `ui` only takes inputs and emits outputs
(no services).

The frontend uses Angular 22 (standalone, zoneless, OnPush by default, signals), Tailwind CSS v4 for the look and
Angular CDK for accessible dialogs and menus ([ADR 0013](docs/adr/0013-ui-tailwind-cdk.md)).

---

## API reference

Base path `/api`. Every endpoint except the token endpoint requires a bearer token (see [Security](#security)). The
full schema is in Swagger.

| Method | Path                                              | Permission       | Success                        | Errors             |
| ------ | ------------------------------------------------- | ---------------- | ------------------------------ | ------------------ |
| POST   | `/auth/token` `{ clientId, clientSecret }`        | anonymous        | 200 `{ accessToken, tokenType, expiresIn }` | 400, 401, 429 |
| GET    | `/.well-known/jwks.json` (outside `/api`; the gateway reads it in-cluster) | anonymous | 200 JWKS: the public key that verifies tokens | – |
| GET    | `/products?page=&pageSize=`                       | `products:read`  | 200 `PagedResponse<Product>`   | 400                |
| GET    | `/products/{id}` (optional `If-None-Match`)       | `products:read`  | 200 `Product` + `ETag`, or 304 if unchanged | 404   |
| GET    | `/products/search?name=&page=&pageSize=`          | `products:read`  | 200 `PagedResponse<Product>`   | 400                |
| GET    | `/products/stock-level?min=&max=&page=&pageSize=` | `products:read`  | 200 `PagedResponse<Product>`   | 400                |
| POST   | `/products`                                       | `products:write` | 201 `Product` + `Location` + `ETag` | 400, 503      |
| PUT    | `/products/{id}` (optional `If-Match`)            | `products:write` | 200 `Product` + `ETag`         | 400, 404, 409, 412 |
| DELETE | `/products/{id}` (optional `If-Match`)            | `products:write` | 204                            | 404, 409, 412      |
| POST   | `/products/{id}/decrement-stock/{quantity}`       | `stock:write`    | 200 `Product` + `ETag`         | 400, 404, 409      |
| POST   | `/products/{id}/add-to-stock/{quantity}`          | `stock:write`    | 200 `Product` + `ETag`         | 400, 404, 409      |
| GET    | `/products/{id}/stock-movements?page=&pageSize=`  | `products:read`  | 200 `PagedResponse<StockMovement>`, newest first | 404 |
| GET    | `/categories`                                     | `products:read`  | 200 `Category[]`               | –                  |

Every endpoint may also answer **401** (no or invalid token), **403** (missing permission) or **429** (rate limited,
with `Retry-After`).


**Product:**

```json
{
  "id": 100004, "name": "Office Lens Desk", "description": "Near-to-intermediate lens optimised for screen work.",
  "price": 189.00, "stockQuantity": 5, "categoryId": 1, "categoryName": "Spectacle Lenses",
  "createdAt": "2026-10-09T13:38:11.29+00:00", "updatedAt": "2026-10-09T13:55:02.10+00:00", "version": 812
}
```

**Idempotency-Key** (optional, recommended) on `POST /products`, `decrement-stock` and `add-to-stock`: one key per
operation (1–128 chars of `A-Z a-z 0-9 - _ . :`, e.g. a UUID), reused when retrying. The operation runs once,
whichever replica the copies reach. Copies get the original response with `Idempotent-Replayed: true`
([ADR 0017](docs/adr/0017-idempotency-keys.md)).

**Conditional GET:** `GET /products/{id}` with `If-None-Match: <ETag>` answers `304 Not Modified` (no body) while the
product is unchanged. It serves clients that call the API directly; through the gateway, the 1 s microcache answers
repeated reads and does not forward conditional headers.

**StockMovement:** `{ id, quantityChange, stockAfter, reason, performedBy, occurredAt }`, with `reason` one of
`InitialStock`, `StockAdded`, `StockRemoved`, `Correction`. A product's stock always equals the sum of its movements
([ADR 0016](docs/adr/0016-stock-ledger.md)).

**Create / update body:** `name` (required, ≤ 200), `description` (≤ 2000), `price` (0.01–1,000,000, ≤ 2
decimals), `stockQuantity` (0–1,000,000), `categoryId` (must exist). `PUT` replaces all fields.

**Paging:** `page` ≥ 1, `pageSize` 1–100 (default 20). Responses have the shape
`{ items, page, pageSize, totalCount, totalPages }`.

**Errors:** always `application/problem+json` with a stable `errorCode`.

| `errorCode`                       | Status | Meaning |
| --------------------------------- | ------ | ------- |
| `validation_failed`               | 400    | see `errors` (field → messages; keys in camelCase as in the request, e.g. `min`, `clientId`) |
| `product.not_found`               | 404    | |
| `product.insufficient_stock`      | 409    | includes `requested` and `available` |
| `product.stock_limit_exceeded`    | 409    | stock would exceed 1,000,000 |
| `product.concurrent_modification` | 409    | changed between read and write (no `If-Match` sent) |
| `product.version_mismatch`        | 412    | `If-Match` no longer matches: reload and retry |
| `product.ids_exhausted`           | 503    | all 900,000 six-digit ids are used: no product can be created until the id range is widened. A 503, not a 409: nothing in the request can fix it, it must alert as a server-side failure, and a retry with the same `Idempotency-Key` stays allowed |
| `invalid_client`                  | 401    | wrong client id or secret (deliberately not saying which) |
| `rate_limited`                    | 429    | see `Retry-After` |
| `too_many_failed_attempts`        | 429    | too many failed sign-ins from this IP; see `Retry-After` |
| `idempotency_key_invalid`         | 400    | the `Idempotency-Key` header is empty, too long or has invalid characters |
| `idempotency_key_in_progress`     | 409    | a copy of this request is still running: retry shortly |
| `idempotency_key_reused`          | 422    | the key was already used for a different request; also detected by the database (the ledger row holding the key) after Redis has forgotten it |
| `idempotency_unavailable`         | 503    | the key store (Redis) is unreachable; the request was not run |
| `http_<status>`                   | any    | framework errors: `http_401`, `http_403`, `http_404` (unknown route), ... |
| `internal_error`                  | 500    | details only in the logs (correlate with `traceId`) |

---

## Security

| Concern        | How |
| -------------- | --- |
| Authentication | The API issues **ES256** JWTs to registered consumers at `POST /api/auth/token` and publishes only its public key (`/.well-known/jwks.json`). **The gateway verifies every token** (signature, `ES256` only, issuer, audience, expiry) and rejects bad ones at the edge; the API verifies again. Consumer secrets are stored only as PBKDF2 hashes; unknown client and wrong secret are indistinguishable ([ADR 0009](docs/adr/0009-authentication-authorization.md), [ADR 0020](docs/adr/0020-edge-token-verification.md)). |
| Authorization  | Permission policies (`products:read`, `stock:write`, `products:write`) mapped from roles (`viewer`, `stock-clerk`, `manager`) in one place. Secure by default: an endpoint without a policy still requires a signed-in user. The UI hides what a role can't do, and the API enforces it. |
| Brute force    | Failed sign-ins are counted per IP in Redis across all replicas: 10 per minute, then `429` even with the right secret. |
| Rate limiting  | The gateway limits per IP; the API per user (stricter for writes), with token buckets **shared by all replicas in Redis**, so the limit does not grow as the autoscaler adds pods. Both answer `429` + `Retry-After` ([ADR 0010](docs/adr/0010-rate-limiting.md)). |
| Network        | NetworkPolicies: the API and the web app accept connections only from the gateway. PostgreSQL and Redis are separate servers outside the cluster, protected by credentials (production adds TLS and a firewall that admits only the cluster's nodes and the CI runner). |
| Database users | Least privilege ([`roles.sql`](infra/postgres/roles.sql)): the API connects as `pm_app` (read and write rows, use the id sequence; no `CREATE`, `ALTER` or `DROP`, 30 s statement timeout, 60 s idle-in-transaction timeout); only the migration step uses the schema owner `pm_migrator`. |
| Cache server   | Redis with a password, no persistence, `volatile-lru`, and `FLUSHALL`, `FLUSHDB` and `DEBUG` disabled ([`redis.conf`](infra/redis/redis.conf)). |
| Containers     | Non-root, read-only root filesystem where possible, no capabilities, no privilege escalation, `RuntimeDefault` seccomp, resource limits. |
| Edge           | Strict **Content-Security-Policy** (`'self'` only, no inline scripts, no framing), `Permissions-Policy`, COOP/CORP, `nosniff`, `X-Frame-Options: DENY`, referrer policy, 1 MB body limit, `server_tokens off` ([ADR 0019](docs/adr/0019-content-security-policy.md)). |
| Privacy        | No third-party requests: the font is self-hosted (no user IPs sent to Google), and browser error reports go to the same origin (`/collect`). The browser cache lives in memory only and is cleared on sign-out. Error reports carry the user id but never the token, form values, query strings or a session id. |
| Exactly-once   | Retries with an `Idempotency-Key` are answered from Redis; the ledger's unique `request_key` makes a second execution impossible even if Redis forgets ([ADR 0017](docs/adr/0017-idempotency-keys.md)). |
| Errors         | Problem details never include stack traces or internals; serializer messages are hidden. |

---

## Performance and scaling

* **Redis cache:** cache-aside with generation-based invalidation, shared by all replicas, fails open
  ([ADR 0008](docs/adr/0008-redis-cache.md)).
* **nginx gateway:** per-request balancing over the API's headless Service (new replicas are picked up within 5 s),
  edge cache for the bundles, **1 s microcache for API reads** (keyed per role from a token the gateway verified, bypassed after the user's own
  changes; 38 % of product GETs served by nginx in the load test), gzip, keep-alive pools
  ([ADR 0012](docs/adr/0012-gateway.md)).
* **Two cache layers, two jobs:** nginx absorbs identical requests within a second; Redis is shared, invalidated
  on every write and keeps entries for minutes.
* **Lists survive stock changes:** the page's ids and count are cached under a generation that only catalogue
  edits move; the rows are read fresh by primary key, so the stock shown is always current
  ([ADR 0008](docs/adr/0008-redis-cache.md)).
* **Autoscaling:** HPA 2–8 replicas, with the pool sized so the maximum stays within what PostgreSQL can serve
  ([ADR 0018](docs/adr/0018-autoscaling-and-scale-testing.md)).
* **Browser cache:** pages already seen open instantly (stale-while-revalidate), the next page is prefetched, and
  paging quickly cancels obsolete requests ([ADR 0007](docs/adr/0007-frontend-state.md)).
* **The database at scale** ([ADR 0022](docs/adr/0022-database-at-scale.md)): every index serves a query the API
  runs (the inventory is pinned by `DatabaseAtScaleTests`); with the ledger grown to **20 million rows**, the last
  page of a busy product's history went from 1.3–6.3 s to 8–22 ms in SQL (late row lookup: ids from the index first,
  then only those rows); per-table autovacuum settings keep index-only scans index-only under constant stock
  changes; the server's memory, WAL and checkpoint settings were sized from the measurements.
* **Capacity on one 8-CPU laptop** (everything, load generator included, on the same machine): **18,177 req/s** of
  catalogue reads (p95 47 ms, 96 % answered by the gateway, which verifies tokens itself and shares its microcache per
  role) and **~2,400 req/s** of the worst-case mix (25 % writes, uniform reads over 850,000 products), both with
  **0 errors** ([ADR 0020](docs/adr/0020-edge-token-verification.md)).
* The load tests sign in as the three demo accounts and lift the per-user rate limits for the run (`rate-limit`
  keeps the real ones): they measure capacity with a few accounts, not many distinct users. The runs below used ten
  load-test accounts, since removed.
* **Load test highlights** ([full results](docs/load-test-results.md)):
  * **ten teams on 850,000 products**, 30 % of stock changes sent twice: **281 req/s, 0 errors, p95 13 ms**;
    17,213 changes applied → exactly 17,213 ledger rows (no duplicate among 6,916 copies); the API scaled 3 → 7.
    The first run at this scale failed (123 req/s, 12 % errors); measuring found an unindexed sort, a cache
    invalidated by every stock change and too many connections, and fixing those got here;
  * p95 of **3 ms** at 150 req/s with 2 replicas;
  * **0 errors in 255k requests** up to ~1,000 req/s, on a single-node 4-vCPU cluster;
  * **300 concurrent orders for 100 units across replicas → exactly 100 sold, stock 0**;
  * scaling the API to 4 replicas exposed PostgreSQL as the bottleneck. Sizing the connection pool to the database's
    capacity and giving it 2 CPUs removed every error.

---

## Key decisions

Each decision has an ADR in [`docs/adr`](docs/adr/README.md):

| #    | Decision |
| ---- | -------- |
| [0001](docs/adr/0001-clean-architecture.md) | Clean architecture, CQRS-lite (projections for reads, aggregates for writes) |
| [0002](docs/adr/0002-product-id-generation.md) | Six-digit ids from a bounded PostgreSQL sequence, safe across instances |
| [0003](docs/adr/0003-atomic-stock-updates.md) | Stock changes as one conditional UPDATE: no overselling, no retries |
| [0004](docs/adr/0004-optimistic-concurrency.md) | Optimistic concurrency with `xmin`, ETag and If-Match |
| [0005](docs/adr/0005-error-handling.md) | Result pattern and RFC 9457 problem details with stable error codes |
| [0006](docs/adr/0006-postgresql.md) | PostgreSQL, tested against the real engine |
| [0007](docs/adr/0007-frontend-state.md) | Frontend state with signals and `rxResource` |
| [0008](docs/adr/0008-redis-cache.md) | Redis read cache with generation-based invalidation |
| [0009](docs/adr/0009-authentication-authorization.md) | API-issued JWTs for registered consumers, permission-based RBAC |
| [0010](docs/adr/0010-rate-limiting.md) | Rate limiting in two layers |
| [0011](docs/adr/0011-kubernetes-deployment.md) | Kubernetes deployment, one manifest per project (superseded in part by 0021) |
| [0012](docs/adr/0012-gateway.md) | nginx gateway in front of the application |
| [0013](docs/adr/0013-ui-tailwind-cdk.md) | Tailwind CSS + Angular CDK instead of Angular Material |
| [0014](docs/adr/0014-mcp-server.md) | MCP server for AI assistants (**removed** on 2026-10-10: not required, not deployed) |
| [0015](docs/adr/0015-load-testing.md) | Load testing with k6 inside the cluster |
| [0016](docs/adr/0016-stock-ledger.md) | Stock ledger written in the same statement as the stock change |
| [0017](docs/adr/0017-idempotency-keys.md) | Idempotency keys for create and stock changes |
| [0018](docs/adr/0018-autoscaling-and-scale-testing.md) | Autoscaling and testing at scale |
| [0019](docs/adr/0019-content-security-policy.md) | Strict Content Security Policy, no third-party assets |
| [0020](docs/adr/0020-edge-token-verification.md) | Token verification at the gateway, microcache shared per role |
| [0021](docs/adr/0021-deployment-layout.md) | Kustomize overlays, servers outside the cluster, migrations in the pipeline |
| [0022](docs/adr/0022-database-at-scale.md) | The database at scale: indexes, vacuum, the ledger beyond 20 million rows |

---

## Assumptions

* Single currency (EUR in the UI), two decimals, prices from 0.01 to 1,000,000.
* Stock is a whole number from 0 to 1,000,000. A decrement larger than the available stock is **rejected** (409),
  not clamped, so the shortage is never hidden from the caller.
* "Return an OK response" for the stock endpoints means **200 with the updated product**.
* Search is a case-insensitive "contains" on the name. Stock-level bounds are inclusive and optional.
* All list endpoints are paged (default 20, maximum 100).
* Categories are reference data, seeded by the migration and read-only through the API.
* Deletion is a hard delete. Ids are never reused.
* Three roles cover the business (`viewer`, `stock-clerk`, `manager`). The set of callers is small and known, so
  consumers live in configuration rather than in a user database or an external identity provider.

---

## Trade-offs and deviations from common practice

| Choice | Why |
| ------ | --- |
| Stock changes bypass load-modify-save | Atomicity under concurrency. The rule stays in the domain; the SQL only applies the range it computes ([0003](docs/adr/0003-atomic-stock-updates.md)). |
| Nullable value types in request DTOs | A missing field becomes a validation error instead of a silent `0`. |
| No `EnableRetryOnFailure` | A blind retry inside the API after an ambiguous commit could apply a stock change twice. Retries belong to the client, with an `Idempotency-Key`. |
| `If-Match` optional | Easy to try with Swagger or curl. The UI always sends it, and making it mandatory (428) is one line. |
| Cache invalidated by generations (data, listing, per product) | Race-free across replicas and O(1); a stock change invalidates only that product and the stock-level lists. Finer than this (per key) was not needed by the measurements ([0008](docs/adr/0008-redis-cache.md)). |
| Per-user rate limits in Redis | One Redis round trip per API request, so a user's limit does not grow as the autoscaler adds replicas; falls back to per-replica limits if Redis is down ([0010](docs/adr/0010-rate-limiting.md)). |
| Real PostgreSQL and Redis in tests (Docker required) | In-memory fakes would hide exactly the behaviour under test. |
| The API issues its own tokens (no identity provider) | One API and a handful of known callers: an IdP would add a component without solving a problem here. ES256: only the API can sign; the gateway and other services verify with the public key ([0009](docs/adr/0009-authentication-authorization.md), [0020](docs/adr/0020-edge-token-verification.md)). |
| Stateless JWTs (no revocation list) | No session store; a 1-hour lifetime and key rotation bound the risk. A `jti` deny-list in Redis is the next step if needed. |
| 1 s nginx microcache | Up to 1 s of staleness for repeated reads, in exchange for absorbing bursts; shared only between callers with the same roles (tokens verified at the gateway), bypassed after the user's own changes. |
| Polling instead of push in the UI | Works across replicas with no extra infrastructure, at the cost of up to 15 s of staleness. |

---

## Production readiness and next steps

**In place:**
* health probes, zero-downtime rolling updates, PodDisruptionBudgets, NetworkPolicies;
* hardened non-root containers, immutable image tags;
* authentication, RBAC, rate limiting, caching, optimistic concurrency, database constraints;
* least-privilege database users; migrations as a pipeline step, before the rollout, with the image being released;
* Kustomize overlays for local, staging and production (Ingress with a cert-manager certificate in staging and
  production), Secrets created by the pipeline from CI/CD variables, never stored in the repository;
* logs of every component, the browser's errors included, in Grafana (Loki, 14 days' retention), with two
  dashboards, 12 alert rules and their runbooks;
* CI with an end-to-end deployment, load tests with SLO thresholds, and one GitLab pipeline per part through staging
  to a manual production step (written, not yet run on a GitLab instance).

**Next** (full assessment: [chapter 11](docs/guide/11-production-readiness.md); runbook: [chapter 12](docs/guide/12-from-zero.md)):

* **Secrets** from a secret manager (External Secrets or Vault) instead of CI/CD variables, for audit and rotation.
* **TLS to the servers verified**: the internal CA mounted in the API pods, then `SSL Mode=VerifyFull`.
* **Backups and restores** of PostgreSQL rehearsed (pgBackRest to object storage, the procedure is in chapter 12);
  later PgBouncer and read replicas.
* **Client IPs** preserved by the load balancer in front of the Ingress controller (`externalTrafficPolicy: Local` /
  PROXY protocol), so per-IP limits apply per caller; a `jti` deny-list (checked at the gateway too) for revocation.
* **Traces and metrics** if logs stop being enough: OpenTelemetry in the API, database and cache exporters on the
  servers ([chapter 8](docs/guide/08-observability.md#what-we-would-add-next)).
* **Keyset pagination** (`?after=`) for constant-time deep pages; monthly partitions for `stock_movements` past
  50–100 million rows ([ADR 0022](docs/adr/0022-database-at-scale.md)).
* A replicated Redis, so idempotency claims survive a Redis restart.
* **GitOps** (Argo CD or Flux) pointed at the overlays instead of `kubectl apply` from the pipeline.

---

## Repository layout

Three GitLab repositories, each self-contained (its own README, `.gitignore` and `.gitlab-ci.yml`) and deployed on
its own:

| Repository | Contents | Folder used in this README |
| --- | --- | --- |
| [gitlab.com/zeiss/backend](https://gitlab.com/zeiss/backend) | the API, its tests, load tests and deploy scripts; the documentation (`docs/`) and this README | `backend/`, `docs/` |
| [gitlab.com/zeiss/frontend](https://gitlab.com/zeiss/frontend) | the web app, the gateway, the Playwright tests and their deploy script | `frontend/` |
| [gitlab.com/zeiss/deploy](https://gitlab.com/zeiss/deploy) | the servers outside the cluster (PostgreSQL, Redis) and the logs (Loki, Grafana, the collectors) | `infra/` |

To run the commands as written, clone them side by side:

```bash
git clone https://gitlab.com/zeiss/backend.git backend
git clone https://gitlab.com/zeiss/frontend.git frontend
git clone https://gitlab.com/zeiss/deploy.git infra
```

The tree below uses those folder names:

```
.
├── backend/                                  # the API (repository backend)
│   ├── ProductManagement.sln                 # solution folders: Source, Tests
│   ├── Dockerfile                            # API image (also runs the migrate, hash-secret, generate-signing-key commands)
│   ├── global.json · Directory.Build.props · Directory.Packages.props · dotnet-tools.json · .editorconfig
│   ├── ProductManagement.Core/               # domain + use cases, by feature: Products/, Categories/, Common/
│   ├── ProductManagement.Infrastructure/     # EF Core, migrations, Redis cache
│   ├── ProductManagement.Api/                # controllers, security, rate limiting, .http requests
│   ├── ProductManagement.UnitTests/          # folders Core/, Infrastructure/, Api/
│   ├── ProductManagement.Api.IntegrationTests/ · ProductManagement.Api.AcceptanceTests/
│   ├── ProductManagement.Testing/            # shared test infrastructure
│   ├── deploy/
│   │   ├── deploy.sh                         # ENVIRONMENT=local|staging|production deploy/deploy.sh: image, migrations, rollout
│   │   ├── local-secrets.sh                  # local signing key, demo consumers, local passwords (generated, gitignored)
│   │   ├── k8s/base/                         # namespace, Deployment (configuration as env keys), Service api, HPA, PDB, NetworkPolicy
│   │   ├── k8s/overlays/{local,staging,production}/   # api.yaml: connection strings and per-environment settings
│   │   └── ci/                               # validate-manifests.sh, create-secrets.sh, deploy-environment.sh
│   ├── load-tests/                           # k6 scenarios + run.sh (runs them as a Kubernetes Job), bulk seed, data checks
│   ├── scripts/check-requirements.py         # the assessment's requirements, checked against the running system
│   └── .gitlab-ci.yml                        # test → build → scan → staging → verify → production
├── frontend/                                 # the web app and the gateway (repository frontend)
│   ├── src/ · e2e/                           # Angular app, Playwright tests
│   ├── Dockerfile · nginx.conf               # web image (static files)
│   ├── gateway/                              # nginx gateway image (Dockerfile, config template, collector.sh, njs: jwt.js, trace.js)
│   ├── deploy/
│   │   ├── deploy.sh                         # [LOGS=off] deploy/deploy.sh: web and gateway images, rollout
│   │   ├── k8s/base/                         # namespace, web.yaml, gateway.yaml
│   │   ├── k8s/overlays/{local,staging,production}/   # gateway.yaml (API and collector addresses), ingress.yaml
│   │   └── ci/validate-manifests.sh
│   └── .gitlab-ci.yml                        # test → build → scan → staging → verify → production
├── infra/                                    # the servers outside the cluster and the logs (repository deploy)
│   ├── infra.sh                              # infra.sh up|down|status [database] [redis] [logs]
│   ├── database.yaml · redis.yaml · logs.yaml   # one Docker Compose file per component
│   ├── postgres/                             # postgresql.conf, roles.sql (the least-privilege users)
│   ├── redis/                                # redis.conf
│   ├── logs/                                 # loki.yaml, grafana/ (dashboards, alert rules), server-agent.alloy,
│   │                                         # k8s/ (the cluster's log collector: Alloy)
│   ├── cluster-addons.sh                     # metrics-server
│   └── .gitlab-ci.yml                        # validate → manual deploy per environment and component
└── docs/                                     # guide, ADRs, flows, architecture, load test results (repository backend)
```

### Tech stack

| Area     | Stack |
| -------- | ----- |
| Backend  | .NET 10, ASP.NET Core, EF Core 10, Npgsql, FluentValidation, StackExchange.Redis, JWT bearer, rate limiting, OpenAPI + Swagger UI |
| Data     | PostgreSQL 18, Redis 8 |
| Security | JWT (ES256) issued by the API and verified at the gateway (nginx njs), PBKDF2 secret hashes, ASP.NET Core authorization and rate limiting |
| Frontend | Angular 22 (standalone, zoneless, signals), Tailwind CSS 4, Angular CDK, Vitest, ESLint, Prettier |
| Testing  | xUnit v3 (Microsoft Testing Platform), Shouldly, NSubstitute, Testcontainers, Respawn, Reqnroll, k6 |
| Delivery | Docker, Docker Compose (the servers), Kubernetes (Docker Desktop locally; k3s as the production target), Kustomize, nginx, GitLab CI/CD, Trivy |
| Operations | Grafana 12, Loki 3, Grafana Alloy (logs only), Grafana Faro (browser errors) |
