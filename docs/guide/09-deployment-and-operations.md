# 9. Deployment and operations

**After this chapter you will know** what runs where (inside the cluster and on the servers outside it), how the
deployment is split into three parts that each deploy on their own, how each part's manifests are split into a base
and one overlay per environment, where the configuration and the secrets come from, what each deploy script does step
by step, how and why migrations run before the rollout and as which database user, what the CI/CD pipelines do, and
the commands for day-to-day operation: rollouts, rollbacks, secret rotation, scaling, troubleshooting, resetting data
and running load tests.

![What runs where: the cluster with the gateway, web, API and Alloy; outside it the CI/CD pipeline, the database, cache and log servers and the server agent](../images/kubernetes-map.svg)

---

## The layout: three parts

The system is in three self-contained parts, each its own GitLab repository with its own pipeline: backend,
frontend and deploy (the folder `infra/`) ([ADR 0021](../adr/0021-deployment-layout.md)). Each one has its code (or configuration), its deploy script
and its manifests together:

```text
infra/                              the servers outside the cluster, and the cluster's log collector
├── infra.sh                        infra.sh up|down|status [database] [redis] [logs]
├── database.yaml                   Docker Compose: PostgreSQL 18 + the one-shot `roles` service
├── redis.yaml                      Docker Compose: Redis 8
├── logs.yaml                       Docker Compose: Loki + Grafana (profile `storage`) and the server agent
├── postgres/                       postgresql.conf, roles.sql (the least-privilege users)
├── redis/                          redis.conf
├── logs/                           loki.yaml, grafana/ (dashboards, alert rules), server-agent.alloy
│   └── k8s/                        the cluster's log collector: Alloy, RBAC, Service alloy-faro, NetworkPolicy
├── cluster-addons.sh               metrics-server
└── .gitlab-ci.yml

backend/                            the API
├── deploy/
│   ├── deploy.sh                   [ENVIRONMENT=local|staging|production] deploy/deploy.sh
│   ├── local-secrets.sh            the local signing key, demo consumers and passwords, generated once per machine
│   ├── k8s/base/                   namespace, deployment.yaml (configuration as env keys), service.yaml (`api`,
│   │                               headless), hpa.yaml, pdb.yaml, network-policy.yaml
│   ├── k8s/overlays/<env>/         kustomization.yaml + api.yaml (connection strings, per-environment settings);
│   │                               local also generates the Secret from generated/ (gitignored)
│   └── ci/                         validate-manifests.sh, create-secrets.sh, deploy-environment.sh
├── load-tests/, scripts/check-requirements.py
└── .gitlab-ci.yml

frontend/                           the web app and the gateway
├── gateway/                        the gateway image (nginx config template, collector.sh, njs)
├── deploy/
│   ├── deploy.sh                   [ENVIRONMENT=...] [LOGS=off] deploy/deploy.sh
│   ├── k8s/base/                   namespace, web.yaml, gateway.yaml
│   ├── k8s/overlays/<env>/         kustomization.yaml + gateway.yaml (API and collector addresses);
│   │                               staging and production add ingress.yaml
│   └── ci/validate-manifests.sh
└── .gitlab-ci.yml
```

Deploy them in this order; each step only needs what the one before it started:

```bash
infra/infra.sh up                 # PostgreSQL, Redis, Loki + Grafana + agents (and the cluster's log collector)
backend/deploy/deploy.sh          # secrets (first run), API image, migrations, rollout
frontend/deploy/deploy.sh         # web + gateway images, rollout → http://localhost:8080
```

**What connects the parts.** Only addresses, written in each part's own files:

| From | To | Where it is set |
| --- | --- | --- |
| backend (the API, the migration step) | PostgreSQL, Redis | `ConnectionStrings__ProductManagement` and `ConnectionStrings__Redis` in `backend/deploy/k8s/overlays/<env>/api.yaml`; `MIGRATION_CONNECTION_STRING` / `MIGRATION_REDIS` (locally) or `DB_SERVER` / `REDIS_SERVER` (pipeline) for the migration |
| backend | the database and Redis passwords | the Secret `api-secrets`; the same values as the infra's `PM_APP_PASSWORD`, `REDIS_PASSWORD` (and `PM_MIGRATOR_PASSWORD` for the migration) |
| frontend (the gateway) | the API | `API_UPSTREAM`, `JWKS_URL` in `frontend/deploy/k8s/overlays/<env>/gateway.yaml`: the Service `api.product-management.svc.cluster.local:8080` |
| frontend (the gateway) | the log collector (optional) | `COLLECTOR_UPSTREAM` in the same file: `alloy-faro.observability.svc.cluster.local:12347`, or `""` with `LOGS=off` |
| infra (the collectors) | Loki | `LOKI_URL`: the local Loki by default, any Loki push URL otherwise |

Both the backend and the frontend apply the namespace `product-management` (the same object): whichever deploys
first creates it.

**Base and overlays.** In the backend and the frontend, `deploy/k8s/base` holds everything that runs in the cluster,
the same in every environment; `deploy/k8s/overlays/<env>` holds only what differs, as one strategic-merge patch per
Deployment (`api.yaml`, `gateway.yaml`) plus, in staging and production, the frontend's Ingress. `kubectl kustomize`
is built into `kubectl`, so nothing else is installed. Each part's `deploy/ci/validate-manifests.sh` checks that every
overlay renders, is valid against the Kubernetes schemas and sets what it must: the backend's both connection strings
(and no Secret outside `local`), the frontend's `API_UPSTREAM`, `JWKS_URL` and `COLLECTOR_UPSTREAM` (and no Secret
at all).

**Configuration is plain environment variables in the Deployment.** The API's settings are `env` entries in
`backend/deploy/k8s/base/deployment.yaml` (ASP.NET Core reads `Section__Key` as `Section:Key`): token lifetime, rate
limits, sign-in throttle, idempotency lifetimes, `Database__ApplyMigrationsOnStartup=false`, `OpenApi__Enabled`. An
overlay's `api.yaml` sets only what differs, and always the two connection strings. Because the values are in the pod
template, changing one rolls the pods like a code change, and `kubectl rollout undo` restores the old configuration
with the old code. The gateway works the same way (`GATEWAY_RATE_LIMIT_*`, `GATEWAY_TRUSTED_PROXIES`, `JWT_*` in the
base; the addresses in the overlays).

**One Secret, no password in a connection string.** The API reads one Secret, `api-secrets`:

| Key | Becomes in the API | Locally | Staging, production |
| --- | --- | --- | --- |
| `database-password` | `PGPASSWORD`: Npgsql uses it when the connection string has no password (the readiness check's own connection too) | `generated/passwords.env` | GitLab `PM_APP_PASSWORD` |
| `redis-password` | `Redis__Password`: the API sets it on the Redis connection, over any password in the connection string | `generated/passwords.env` | GitLab `REDIS_PASSWORD` |
| `signing-key` | `Authentication__SigningKey`: the ES256 private key that signs tokens | `generated/signing-key.pem` | GitLab `API_SIGNING_KEY` (File) |
| `consumers.json` | the file `/app/config/consumers.json`: client ids, PBKDF2 hashes, roles | `generated/consumers.json` | GitLab `API_CONSUMERS` (File) |

Locally `backend/deploy/local-secrets.sh` writes the three files into `backend/deploy/k8s/overlays/local/generated/`
(gitignored, mode 600) once per machine, and the overlay's `secretGenerator` makes `api-secrets-<hash>` from them: a
changed file gives the Secret a new name, which changes the Deployment and rolls the pods. In staging and production
the overlays contain no Secret; the deploy job creates `api-secrets` from CI/CD variables
(`backend/deploy/ci/create-secrets.sh`).

**Servers outside the cluster.** PostgreSQL, Redis and the log server usually live outside Kubernetes (a dedicated
server, or a managed service such as Amazon RDS, ElastiCache or Grafana Cloud), with their own backups, upgrades and
owners. So here they run outside the cluster too: locally `infra/infra.sh` starts each one as a Docker Compose
container with the same configuration file a real server would use. The cluster runs only stateless workloads and
the log collector, so it can be rebuilt from Git at any time without touching the data. The API's connection strings
name the servers directly (`host.docker.internal` locally, the private DNS names `pm-db…`, `pm-cache…` elsewhere).

**Logs are optional.** The `logs` component (Loki, Grafana, the server agent and the cluster's collector) can be left
out: `infra/infra.sh up database redis` and `LOGS=off frontend/deploy/deploy.sh`. The application works the same; the
pods log to stdout (`kubectl logs`) and the gateway answers the browser's error reports with 204. With
`LOKI_URL=… infra/infra.sh up logs` the logs go to another Loki instead (no local Loki or Grafana).

## What runs where

**Outside the cluster** (locally: containers started by `infra/infra.sh up`, one Compose project per component):

| Container | Component | What it is |
| --- | --- | --- |
| `pm-postgres` | database (`database.yaml`) | PostgreSQL 18, volume `pm-postgres-data`, 4 CPUs and 3 GiB, `max_connections=200`, `shared_buffers=768MB`, `pg_stat_statements` preloaded, slow statements (> 250 ms), lock waits and autovacuum runs logged; sized from the measurements in [ADR 0022](../adr/0022-database-at-scale.md) |
| `pm-postgres-roles` | database | one-shot, on every `up`: creates `pg_stat_statements` and applies `roles.sql`, then exits |
| `pm-redis` | redis (`redis.yaml`) | Redis 8, password protected (`--requirepass`), memory only, `maxmemory 256mb`, `volatile-lru` (only keys with an expiry can be evicted), `FLUSHALL`/`FLUSHDB`/`DEBUG` disabled |
| `pm-loki`, `pm-grafana` | logs (`logs.yaml`, profile `storage`) | Loki 3.5.5 (port 3100, 14 days, volume `pm-loki-data`) stores the logs; Grafana 12.2.0 (port 3000, volume `pm-grafana-data`) shows them, with the dashboards and alert rules provisioned from `infra/logs/grafana` ([chapter 8](08-observability.md)) |
| `pm-server-agent` | logs | Grafana Alloy: ships the server containers' logs (PostgreSQL, Redis, Loki, Grafana) to `LOKI_URL`, as the agent installed on each server would |

The volumes are created by `infra.sh` and declared `external` in the Compose files, so `docker compose down -v`
cannot delete the data.

**Database users** ([`roles.sql`](../../infra/postgres/roles.sql), applied by `infra/infra.sh up database`):
`pm_migrator` owns the schema and is used only to run migrations; `pm_app` is the API's user. It can read and write
rows and draw ids from the sequence, nothing else (`CREATE TABLE`: "permission denied for schema public"; `ALTER`,
`DROP`: "must be owner"), a statement is cancelled after 30 s and a transaction left idle after 60 s. The server's
administrator is used by the infra and by people, never by an application.

**Namespace `product-management`:**

| Object | Kind | Part | What it does |
| --- | --- | --- | --- |
| `api-secrets` (locally `api-secrets-<hash>`) | Secret | backend (local overlay / pipeline) | The database and Redis passwords, the signing key, the consumers (above). |
| `api` | Deployment (no fixed count) | backend | The API pods, with their configuration as env keys. Rolling update with `maxUnavailable: 0`, a 5 s `preStop` pause, startup, readiness and liveness probes, 5 old ReplicaSets kept. Pool of 10 database connections per pod, plus one for the readiness check. |
| `api` | Service (headless, `clusterIP: None`) | backend | **The API's only Service.** `api.product-management.svc.cluster.local` lists every ready API pod; the gateway balances over them per request and downloads the public signing key from it ([chapter 2](02-request-journey.md#why-the-apis-service-is-headless)). |
| `api` | HorizontalPodAutoscaler | backend | 2–8 pods at 60 % CPU ([chapter 6](06-scaling-and-capacity.md)). |
| `api` | PodDisruptionBudget | backend | At least one pod stays up during node maintenance. |
| `api-ingress` | NetworkPolicy | backend | Only the gateway may connect. |
| `web` | Deployment (2 pods) + Service + PDB | frontend | nginx serving the Angular build. |
| `web-ingress` | NetworkPolicy | frontend | Only the gateway may connect. |
| `gateway` | Deployment (no fixed count) + PDB + HPA | frontend | The nginx gateway, 2–6 pods at 70 % CPU. |
| `gateway` | Service | frontend | Port 8080. Locally type `LoadBalancer` (`localhost:8080` on Docker Desktop). |
| `gateway` | Ingress | frontend (staging, production) | HTTPS for `staging.products.example.com` / `products.example.com`, certificate from cert-manager (Let's Encrypt). |

**Namespace `observability`** (infra, `infra/logs/k8s`, applied by `infra/infra.sh up logs`): `alloy` (Deployment,
1 pod, with a ServiceAccount that may read pods and their logs), which collects every pod's log and pushes it to
`LOKI_URL` (default `http://host.docker.internal:3100/loki/api/v1/push`, the local Loki; credentials for a hosted
Loki in the Secret `loki-credentials`). It also receives the browser's error reports through the Service `alloy-faro`
(port 12347), which the gateway's `/collect` proxies to and a NetworkPolicy (`alloy-ingress`) reserves for the
gateway. [Chapter 8](08-observability.md) describes what it collects and how.

**`kube-system`:** metrics-server, installed once per cluster by `infra/cluster-addons.sh` (built into k3s and most
managed clusters), which feeds the two HPAs and `kubectl top`.

Nothing in the cluster holds data: no StatefulSet, no volume, no database credential that can change the schema.

## The deploy scripts, step by step

### `infra/infra.sh`

`infra/infra.sh up|down|status [database] [redis] [logs]`; no component means all three, in that order.

| Component | `up` | `down` |
| --- | --- | --- |
| `database` | create the volume `pm-postgres-data` → `docker compose -f database.yaml up -d` → wait for the `roles` container's result (it fails the script if `roles.sql` failed) | `docker compose down` |
| `redis` | `docker compose -f redis.yaml up -d --wait` | `docker compose down` |
| `logs` | without `LOKI_URL`: create `pm-loki-data`, `pm-grafana-data` → `docker compose -f logs.yaml --profile storage up -d` → wait for Loki and Grafana; with `LOKI_URL`: only the agent, shipping there. Then the cluster's collector: render `infra/.build/logs-k8s` (with a `LOKI_URL` patch and the Secret `loki-credentials` when it is set) → `kubectl apply -k` → wait for the rollout | remove the containers, and `kubectl delete -k logs/k8s` (the `observability` namespace) |

`logs` skips what it cannot do where it runs: the containers without `docker`, the cluster's collector without a
`kubectl` context. `status` lists the containers labelled `com.product-management.role=server` and the collector.
Local password defaults (`POSTGRES_PASSWORD`, `PM_MIGRATOR_PASSWORD`, `PM_APP_PASSWORD`, `REDIS_PASSWORD`) can be
overridden in the environment or in `infra/.env`.

### `backend/deploy/deploy.sh`

`ENVIRONMENT` picks the overlay (`local` by default). Locally:

1. **Local secrets** (first run only): `backend/deploy/local-secrets.sh` creates what is missing in
   `backend/deploy/k8s/overlays/local/generated/`, and the API's .NET user-secrets for `dotnet run`.
2. **Image:** builds `backend/` → `product-management/api:<tag>` and publishes it to the cluster (below).
3. **Migrations:** runs that image once with `migrate`, as `pm_migrator` (below).
4. **Rollout:** writes `backend/deploy/k8s/.build/<env>/kustomization.yaml` (not committed), which takes the overlay
   and pins this release's image with Kustomize `images`; renders it with `kubectl kustomize`, pipes it to
   `kubectl apply`, and waits for `deployment/api`.

| Variable | Effect |
| --- | --- |
| `ENVIRONMENT` | `local` (default), `staging` or `production` |
| `API_IMAGE` | an image already built and pushed (`registry/name:tag` or `name@sha256:…`): no local build |
| `MIGRATIONS=skip` | the pipeline ran the migrations in its own job |
| `MIGRATION_CONNECTION_STRING`, `PM_MIGRATOR_PASSWORD` | where and as whom the migration connects (default: the local database as `pm_migrator`, `local-migrator-password`) |
| `MIGRATION_REDIS`, `REDIS_PASSWORD` | the Redis the migration invalidates (default: `host.docker.internal:6379`, `local-redis-password`) |
| `SEED_SAMPLE_DATA` | sample catalogue on an empty database (default `true`) |

### `frontend/deploy/deploy.sh`

Locally: builds `frontend/` → `product-management/web:<tag>` (with `APP_VERSION=<tag>`, the release the browser
reports with its errors) and `frontend/gateway/` → `product-management/gateway:<tag>`, publishes both, writes
`frontend/deploy/k8s/.build/<env>/kustomization.yaml` with both images pinned, applies it and waits for both
rollouts. Variables: `ENVIRONMENT`, `WEB_IMAGE` and `GATEWAY_IMAGE` (no local build), and `LOGS=off`, which adds a
patch that sets the gateway's `COLLECTOR_UPSTREAM` to `""`: the gateway's `collector.sh` then makes `/collect` answer
204 and drop the reports instead of proxying to a collector that is not there.

**What a deploy does not do.** `kubectl apply` does not prune: an object removed from the manifests stays in the
cluster until it is deleted by hand (`kubectl delete <kind> <name>`), in each environment. Locally the old
`api-secrets-<hash>` Secrets also stay (they let `kubectl rollout undo` go back to the previous passwords and keys);
delete old ones with `kubectl -n product-management get secrets` and `kubectl delete secret`.

## Image tags

Each deploy script decides two things for local deployments (the pipelines tag images with the commit's short SHA
and push them to the project's registry):

* **The tag:** the commit's short hash (`git rev-parse --short HEAD`), plus `-dirty-<timestamp>` when the working tree
  has uncommitted changes, so two different builds never share a tag.
* **Where the image comes from:** on Docker Desktop (context `docker-desktop`) the cluster runs on the same Docker
  engine that built the image, so nothing is pushed. Any other context pulls from a registry, so the script needs the
  image references (`API_IMAGE`; `WEB_IMAGE` and `GATEWAY_IMAGE`), as the pipelines pass them, and stops with a
  message when they are missing.

## Migrations run before the rollout, with the image being deployed

`backend/deploy/deploy.sh` runs the freshly built API image once, outside the cluster, with the argument `migrate`
([`ApiCommandLine.cs`](../../backend/ProductManagement.Api/Hosting/ApiCommandLine.cs)), as the schema owner. The
passwords are separate variables, never part of a connection string:

```bash
docker run --rm --add-host=host.docker.internal:host-gateway -e ASPNETCORE_ENVIRONMENT=Production \
  -e ConnectionStrings__ProductManagement="Host=host.docker.internal;Port=5432;Database=product_management;Username=pm_migrator;GSS Encryption Mode=Disable" \
  -e PGPASSWORD="${PM_MIGRATOR_PASSWORD:-local-migrator-password}" \
  -e ConnectionStrings__Redis="host.docker.internal:6379" -e Redis__Password="${REDIS_PASSWORD:-local-redis-password}" \
  product-management/api:<tag> migrate
```

It waits for PostgreSQL (up to 30 attempts, 2 s apart), applies the EF Core migrations, seeds the sample data into an
empty database, and moves the Redis cache generations on (the data may have changed underneath the cache). Then it
exits, and only then is the new API rolled out. In the backend's GitLab pipeline the same command is the
`migrate:<environment>` job, run with the release's image on a runner inside the private network, with `DB_SERVER`,
`REDIS_SERVER`, `PM_MIGRATOR_PASSWORD` and `REDIS_PASSWORD`.

* **The code that migrates is exactly the code being deployed**, and it runs once per deployment, not once per pod.
  The API pods start with `ApplyMigrationsOnStartup=false`. The cluster needs no migration Job and holds no database
  credential that can change the schema: its `pm_app` cannot.
* **Large indexes are built `CONCURRENTLY`** by the migrations that add them, and storage parameters (the per-table
  autovacuum settings of [ADR 0022](../adr/0022-database-at-scale.md)) are instant `ALTER TABLE … SET`, so the tables
  keep accepting writes.
* **It needs only the database** (and Redis, optionally). The `migrate` command builds a plain host with the
  infrastructure services, without the web server, authentication or rate limiting, so it is never given the key that
  signs access tokens.
* **Expand/contract.** The old pods keep running while the migration applies, and during the rollout old and new
  pods run side by side. A migration must therefore keep working with the version still running (add first, remove in
  a later release). A rollback never undoes a migration; a bad one is fixed forward.

## CI/CD pipelines

One GitLab pipeline per repository (backend, frontend, deploy), the only CI/CD: it tests every merge request and
delivers to staging and production. Every step it runs is a script in that repository that also runs on a laptop. The
deploy jobs reach the clusters through the GitLab agent for Kubernetes (`KUBE_CONTEXT`), so no cluster API is exposed
to the runners, or with a kubeconfig (a File variable `KUBECONFIG`) from a self-hosted runner next to the cluster.
The staging and production jobs exist only once the environment is switched on (project or group variables
`STAGING_ENABLED` / `PRODUCTION_ENABLED` = `true`); until then a pipeline on `main` stops, green, after scan. The deploy
jobs store the project's deploy token (`gitlab-deploy-token`, `read_registry`) as the Secret the pods pull the private
images with (`imagePullSecrets` in the staging and production overlays).

*Backend* ([`backend/.gitlab-ci.yml`](../../backend/.gitlab-ci.yml)):

| Stage | Jobs | Runs on |
| --- | --- | --- |
| test | `test:backend` (`dotnet format --verify-no-changes`, Release build with warnings as errors, every test suite, the integration and acceptance tests with PostgreSQL and Redis from Testcontainers on Docker-in-Docker, JUnit), `test:manifests` (`deploy/ci/validate-manifests.sh` in the `alpine/k8s` image: render, `kubeconform -strict` against the Kubernetes 1.34 schemas, the settings each overlay must set, no Secret where there must be none), GitLab SAST and secret detection | merge requests, `main`, tags |
| build | `build:api` → `$CI_REGISTRY_IMAGE/api:<short sha>`; `migrations:sql` (`dotnet restore`, then the idempotent SQL of every migration, for review in the merge request) | merge requests, `main`, tags |
| scan | `scan:api`: Trivy, fails on fixable critical vulnerabilities | merge requests, `main`, tags |
| staging | `migrate:staging` (API image, `pm_migrator`, private runner) → `deploy:staging` (`deploy/ci/deploy-environment.sh`: selects the cluster, the Secrets `gitlab-registry-backend` from the deploy token and `api-secrets` from CI/CD variables, then `deploy/deploy.sh` with the pipeline's image and `MIGRATIONS=skip`; restarts the API when `api-secrets` changed) | `main` with `STAGING_ENABLED`, automatic |
| verify | `verify:requirements` (`python3 scripts/check-requirements.py` against `STAGING_URL`) | `main` with `STAGING_ENABLED` |
| production | `migrate:production` (**manual**, protected environment) → `deploy:production` | `main` with `STAGING_ENABLED` and `PRODUCTION_ENABLED` |

*Frontend* ([`frontend/.gitlab-ci.yml`](../../frontend/.gitlab-ci.yml)):

| Stage | Jobs | Runs on |
| --- | --- | --- |
| test | `test:frontend` (`npm run lint`, `format:check`, `test:ci`, `build`), `test:manifests` (the same checks on the web and gateway overlays), SAST and secret detection | merge requests, `main`, tags |
| build | `build:web`, `build:gateway` → `$CI_REGISTRY_IMAGE/{web,gateway}:<short sha>` | merge requests, `main`, tags |
| scan | `scan:web`, `scan:gateway` (Trivy) | merge requests, `main`, tags |
| staging | `deploy:staging` (`deploy/ci/deploy-environment.sh`: selects the cluster, the Secret `gitlab-registry-frontend` from the deploy token, then `deploy/deploy.sh` with the pipeline's images) | `main` with `STAGING_ENABLED`, automatic |
| verify | `verify:e2e` (Playwright, desktop and phone, against `STAGING_URL`) | `main` with `STAGING_ENABLED` |
| production | `deploy:production` (**manual**, protected environment) | `main` with `STAGING_ENABLED` and `PRODUCTION_ENABLED` |

*Infra* ([`infra/.gitlab-ci.yml`](../../infra/.gitlab-ci.yml)): `validate` on every change (the three Compose files,
the log collector's manifests with `kubeconform`, `alloy fmt` on both Alloy configurations); then manual deploy jobs
on the default branch, per environment once it is switched on: `servers:staging` / `servers:production`, a matrix over `COMPONENT` (`database`, `redis`,
`logs`) that runs `./infra.sh up <component>` on a shell runner on that server (tag `pm-<environment>-<component>`),
and `cluster-logs:staging` / `cluster-logs:production` (`ci/cluster-logs.sh`), which apply the cluster's collector
through the agent or a kubeconfig with `LOKI_URL=$CLUSTER_LOKI_URL`.

Runners: every job without tags runs on gitlab.com's shared runners; `migrate:*` needs the self-hosted runner in the
private network (`PRIVATE_RUNNER_TAG`, default `product-management-private`), and infra's `servers:*` a shell runner
on each server. The pipelines run on gitlab.com; the staging and production jobs have not yet run against real
clusters. Setting them up (runners, the agent or a kubeconfig, environments, every variable per project, or one
machine of our own) is [chapter 12](12-from-zero.md).

## Day-2 operations

All commands run from the folder that holds the three repositories (`backend/`, `frontend/`, `infra/`) and assume
`kubectl config use-context docker-desktop`.

**See what is running**

```bash
kubectl -n product-management get pods -o wide
kubectl -n product-management get hpa
kubectl -n product-management top pods                       # needs metrics-server
kubectl -n product-management get events --sort-by=.lastTimestamp | tail -20
infra/infra.sh status                                        # the servers and the cluster's log collector
```

**Logs.** In Grafana (http://localhost:3000, [chapter 8](08-observability.md)), or directly (this works with the logs
component down too):

```bash
kubectl -n product-management logs -l app.kubernetes.io/name=api --tail=20 --prefix
kubectl -n product-management logs -l app.kubernetes.io/name=gateway --tail=20   # access log: upstream pod, cache status, trace_id
docker logs pm-postgres --tail 50                                    # slow statements, lock waits, errors
```

**Roll out a change.** Run the part's deploy script again; it builds, (for the API) migrates and rolls out:

```bash
backend/deploy/deploy.sh          # after changing the API, its configuration (deployment.yaml, api.yaml) or passwords.env
frontend/deploy/deploy.sh         # after changing the web app, the gateway or gateway.yaml
infra/infra.sh up database        # after changing postgresql.conf or roles.sql (also re-applies the users' passwords)
kubectl kustomize backend/deploy/k8s/overlays/local | less          # what would be applied
kubectl -n product-management rollout status deployment/api
```

**Change a setting.** Edit the env entry in `backend/deploy/k8s/base/deployment.yaml` (every environment) or in
`backend/deploy/k8s/overlays/<env>/api.yaml` (one environment), then deploy. To try a value without a deploy,
`kubectl -n product-management set env deployment/api RateLimiting__RequestsPerSecond=100` rolls the pods with it;
the next deploy puts the file's value back.

**Roll back** (each Deployment keeps 5 old ReplicaSets, each with its own image and configuration):

```bash
kubectl -n product-management rollout history deployment/api
kubectl -n product-management rollout undo deployment/api
kubectl -n product-management rollout undo deployment/gateway
```

A rollback does not undo database migrations; that is why migrations must stay compatible with the previous version.
In staging and production, roll back from GitLab instead: Operate → Environments → the environment → re-deploy an
earlier successful deployment, which re-runs that pipeline's deploy job with its images and manifests (and the
current Secret).

**Rotate a secret.**

* *Staging and production:* change the GitLab variable (`PM_APP_PASSWORD`, `REDIS_PASSWORD`, `API_SIGNING_KEY`,
  `API_CONSUMERS`) and re-run the backend's last deploy job. `create-secrets.sh` updates `api-secrets` and prints
  `api-secrets` when the content changed; `deploy-environment.sh` then runs `kubectl rollout restart deployment/api`
  so the pods read it. Database and Redis passwords change on the servers first (the infra's `roles.sql` with the new
  value, or the Redis password); the order and how to avoid a gap: [chapter 12](12-from-zero.md#rotate-secrets).
* *Locally:* edit `backend/deploy/k8s/overlays/local/generated/passwords.env` (or
  `FORCE=1 PM_APP_PASSWORD=... REDIS_PASSWORD=... backend/deploy/local-secrets.sh`, which also makes a new signing key
  and consumer hashes and signs everyone out), restart the servers with the same values
  (`PM_APP_PASSWORD=... REDIS_PASSWORD=... infra/infra.sh up database redis`), then `backend/deploy/deploy.sh`: the
  Secret gets a new name and the pods roll.

**Scale.** The HPAs own the replica counts, so `kubectl scale` is undone within seconds. Change the range instead
(for good: in `backend/deploy/k8s/base/hpa.yaml`, or `minReplicas`/`maxReplicas` of the gateway's HPA in
`frontend/deploy/k8s/base/gateway.yaml`):

```bash
kubectl -n product-management patch hpa api -p '{"spec":{"minReplicas":4,"maxReplicas":8}}'
kubectl -n product-management get hpa api -w
```

Keep `maxReplicas × (10 + 1)` connections (the request pool plus the readiness check's own connection) within what
PostgreSQL can serve: 8 × 11 = 88 of its 200
([chapter 6](06-scaling-and-capacity.md#why-the-database-is-the-ceiling)).

**Swagger UI** (enabled in the local and staging overlays)

```bash
kubectl -n product-management port-forward deployment/api 5080:8080   # http://localhost:5080/swagger, then Authorize with a token
```

**Database users.** After changing `roles.sql`, or to rotate a local password: `infra/infra.sh up database` (the
`roles` service applies it on every `up`).

**Reset the data** (deletes everything in PostgreSQL):

```bash
infra/infra.sh down database
docker volume rm pm-postgres-data
infra/infra.sh up database          # new empty database with its users
backend/deploy/deploy.sh            # the migrations re-create the schema and the sample data
```

**Bulk data and load tests**

```bash
infra/cluster-addons.sh                           # metrics-server, once per cluster
backend/load-tests/seed-bulk.sh                   # adds 850,000 products and ~5 million movements (minutes)
PRODUCTS=0 LEDGER_ROWS=20000000 backend/load-tests/seed-bulk.sh   # grows the ledger to 20 million rows (~20 minutes)
backend/load-tests/verify-data.sh                 # data invariants, all must be 0
backend/load-tests/run.sh smoke                   # any scenario: smoke, load, stress, spike, stock-consistency, rate-limit, teams, browse
```

`run.sh` raises the API's rate limits (and, for some scenarios, the gateway's) with `kubectl set env` for the run,
after saving the deployed values, and always sets those values back at the end.

**Troubleshooting**

| Symptom | Look at |
| --- | --- |
| API pods not ready | `kubectl -n product-management describe pod -l app.kubernetes.io/name=api`, then `kubectl -n product-management logs deployment/api`; readiness includes the database (`SELECT 1`), so check the server: `infra/infra.sh status`, `docker logs pm-postgres --tail 50` |
| The API cannot reach the database or Redis | the connection strings it runs with: `kubectl -n product-management get deployment api -o jsonpath='{.spec.template.spec.containers[0].env}'`; whether the cluster reaches the host: `kubectl run nettest --rm -it --restart=Never --image=busybox:1.37 -- nc -zv host.docker.internal 5432` |
| Password authentication failed | the Secret's keys (`kubectl -n product-management get secrets`, then `kubectl -n product-management describe secret <name>`) against the servers' values: locally `passwords.env` must match the infra's `PM_APP_PASSWORD` / `REDIS_PASSWORD`; `infra/infra.sh up database` re-applies the users' passwords |
| Migration step fails | its output in the deploy script's terminal (or the `migrate:*` job log); `PM_MIGRATOR_PASSWORD` must match the infra's |
| `502` on `/api` | no ready API pod: `kubectl -n product-management get endpointslices -l kubernetes.io/service-name=api` |
| `502` on `/collect` in the browser console | no log collector: `infra/infra.sh up logs`, or deploy the frontend with `LOGS=off` |
| `ImagePullBackOff` | outside Docker Desktop, the image must be in a registry the cluster can pull from: the `API_IMAGE` / `WEB_IMAGE` / `GATEWAY_IMAGE` given to the deploy script, and the cluster's pull credentials |
| HPA shows `<unknown>` | metrics-server: `infra/cluster-addons.sh`, then `kubectl top pods` |

## Where to look

* Deploy scripts: [`infra/infra.sh`](../../infra/infra.sh), [`backend/deploy/deploy.sh`](../../backend/deploy/deploy.sh),
  [`backend/deploy/local-secrets.sh`](../../backend/deploy/local-secrets.sh),
  [`frontend/deploy/deploy.sh`](../../frontend/deploy/deploy.sh)
* Backend manifests: [`backend/deploy/k8s/base`](../../backend/deploy/k8s/base)
  ([`deployment.yaml`](../../backend/deploy/k8s/base/deployment.yaml), [`service.yaml`](../../backend/deploy/k8s/base/service.yaml)),
  the overlays [`local`](../../backend/deploy/k8s/overlays/local/kustomization.yaml),
  [`staging`](../../backend/deploy/k8s/overlays/staging/api.yaml),
  [`production`](../../backend/deploy/k8s/overlays/production/api.yaml)
* Frontend manifests: [`frontend/deploy/k8s/base`](../../frontend/deploy/k8s/base), the overlays
  [`local`](../../frontend/deploy/k8s/overlays/local/gateway.yaml),
  [`staging`](../../frontend/deploy/k8s/overlays/staging/kustomization.yaml),
  [`production`](../../frontend/deploy/k8s/overlays/production/kustomization.yaml);
  [`gateway/collector.sh`](../../frontend/gateway/collector.sh)
* Servers: [`infra/database.yaml`](../../infra/database.yaml), [`infra/redis.yaml`](../../infra/redis.yaml),
  [`infra/logs.yaml`](../../infra/logs.yaml), [`postgresql.conf`](../../infra/postgres/postgresql.conf),
  [`roles.sql`](../../infra/postgres/roles.sql), [`redis.conf`](../../infra/redis/redis.conf),
  the cluster's collector [`infra/logs/k8s`](../../infra/logs/k8s)
* Migrations: [`ApiCommandLine.cs`](../../backend/ProductManagement.Api/Hosting/ApiCommandLine.cs),
  [`DatabaseInitializer.cs`](../../backend/ProductManagement.Infrastructure/Persistence/DatabaseInitializer.cs),
  [`Persistence/Migrations`](../../backend/ProductManagement.Infrastructure/Persistence/Migrations)
* Cluster helper: [`infra/cluster-addons.sh`](../../infra/cluster-addons.sh) (metrics-server)
* CI/CD: [`backend/.gitlab-ci.yml`](../../backend/.gitlab-ci.yml), [`frontend/.gitlab-ci.yml`](../../frontend/.gitlab-ci.yml),
  [`infra/.gitlab-ci.yml`](../../infra/.gitlab-ci.yml), [`backend/deploy/ci`](../../backend/deploy/ci),
  [`frontend/deploy/ci`](../../frontend/deploy/ci)
* Each part on its own: [`backend/README.md`](../../backend/README.md), [`frontend/README.md`](../../frontend/README.md),
  [`infra/README.md`](../../infra/README.md)
* Decisions: [ADR 0021 deployment layout](../adr/0021-deployment-layout.md) (which supersedes most of
  [ADR 0011](../adr/0011-kubernetes-deployment.md))
* Diagrams: [flows.md §9 deployment and CI/CD](../flows.md#9-deployment-and-cicd),
  [architecture.md](../architecture.md#deployment-topology)

## How to verify

```bash
infra/infra.sh up && backend/deploy/deploy.sh && frontend/deploy/deploy.sh
infra/infra.sh status
kubectl -n product-management get deploy,svc,hpa,pdb,networkpolicy,secret
kubectl -n product-management get svc api -o jsonpath='{.spec.clusterIP}'; echo      # None: headless
kubectl -n product-management get deploy api -o jsonpath='{.spec.template.spec.containers[0].image}'; echo   # the exact tag

# No password in the API's configuration: the connection strings have none, the passwords come from the Secret
kubectl -n product-management get deploy api -o jsonpath='{range .spec.template.spec.containers[0].env[*]}{.name}={.value}{"\n"}{end}' | grep -i -E 'connection|password'

# The API's database user cannot change the schema
docker exec -e PGPASSWORD=local-app-password pm-postgres psql -h localhost -U pm_app -d product_management \
  -c 'BEGIN' -c 'DROP TABLE products' -c 'ROLLBACK'          # ERROR: must be owner of table products

# Every overlay renders and is valid (what CI runs)
docker run --rm -v "$PWD:/work:ro" -w /work alpine/k8s:1.34.11 backend/deploy/ci/validate-manifests.sh
docker run --rm -v "$PWD:/work:ro" -w /work alpine/k8s:1.34.11 frontend/deploy/ci/validate-manifests.sh

# Without logs: /collect answers 204
infra/infra.sh down logs && LOGS=off frontend/deploy/deploy.sh
curl -s -o /dev/null -w '%{http_code}\n' -X POST localhost:8080/collect -H 'Content-Type: application/json' -d '{}'   # 204
```
